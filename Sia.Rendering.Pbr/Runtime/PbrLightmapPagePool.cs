namespace Sia.Engine.Rendering.Pbr;

// Queue operations stay at the rendering use site. This owner has no asset IO or World mutations.
internal delegate void PbrLightmapPageWrite(int slot, int edge, int firstTexel, ReadOnlySpan<uint> coefficients);

internal sealed class PbrLightmapPagePool
{
    private struct Entry
    {
        public StreamRange Range;
        public int Page;
        public bool Published, Retired;
        public Task<bool>? Completion;
    }

    internal const int TexelBytes = PbrLightmapPageData.PackedTexelWords * sizeof(uint);
    internal const int SlotBytes = PbrLightmapPageData.MaximumEdge * PbrLightmapPageData.MaximumEdge * TexelBytes;
    private readonly Action<int,PbrLightmapPageData> _validate;
    private readonly StreamRangePool _ranges;
    private readonly Entry[] _entries;
    private readonly int[] _allocated;
    private readonly uint[] _scratch;
    private readonly PbrLightmapPageWrite _write;
    private readonly Action<int, int> _map;
    private readonly Func<Task<bool>> _afterSubmittedWork;
    private int _uploadSlot = -1, _edge, _texels;
    private Exception? _failure;
    private Task? _stop;
    private bool _stopping;

    internal int ActivePage => _uploadSlot < 0 ? -1 : _entries[_uploadSlot].Page;
    internal int OccupiedSlots => (int)_ranges.Used;
    internal int Capacity => _entries.Length;
    internal int ResidentPages { get; private set; }
    internal int RetiringSlots { get; private set; }
    internal int ScratchBytes => SlotBytes;
    internal long Installs { get; private set; }
    internal long Evictions { get; private set; }
    internal ulong Revision { get; private set; }
    internal bool IsStopped { get; private set; }

    internal PbrLightmapPagePool(int pages,Action<int,PbrLightmapPageData> validate,int slots,PbrLightmapPageWrite write,
        Action<int, int> map, Func<Task<bool>> afterSubmittedWork)
    {
        ArgumentNullException.ThrowIfNull(validate); ArgumentNullException.ThrowIfNull(write);
        ArgumentNullException.ThrowIfNull(map); ArgumentNullException.ThrowIfNull(afterSubmittedWork);
        if (pages is <1 or >PbrLightmapData.MaximumPages || slots<1 || slots>pages) throw new ArgumentOutOfRangeException(nameof(slots));
        _validate=validate; _ranges = new((uint)slots); _entries = new Entry[slots];
        _allocated = new int[pages]; _scratch = new uint[SlotBytes / sizeof(uint)];
        _write = write; _map = map; _afterSubmittedWork = afterSubmittedWork;
    }

    internal int ResidentPage(int slot)
    {
        if ((uint)slot >= _entries.Length) throw new ArgumentOutOfRangeException(nameof(slot));
        return _entries[slot].Published ? _entries[slot].Page : -1;
    }

    internal int ResidentSlot(int page)
    {
        RequirePage(page);
        var slot = _allocated[page] - 1;
        return slot >= 0 && _entries[slot].Published ? slot : -1;
    }

    // The decoded page is borrowed only during this call. One fixed packing scratch owns the upload.
    internal bool BeginUpload(int page, PbrLightmapPageData data)
    {
        RequireRunning(); RequirePage(page); _validate(page,data);
        if (_allocated[page] != 0 || _uploadSlot >= 0 || !_ranges.TryAllocate(1, out var range)) return false;
        try { data.CopyPackedCoefficients(_scratch.AsSpan(0, data.TexelCount * PbrLightmapPageData.PackedTexelWords)); }
        catch { _ranges.Free(range); throw; }
        _uploadSlot = (int)range.Offset; _edge = data.Edge; _texels = 0;
        _entries[_uploadSlot] = new() { Range = range, Page = page };
        _allocated[page] = _uploadSlot + 1;
        return true;
    }

    // All coefficient writes precede the nonzero mapping write on the same queue.
    // A mapping costs four bytes and is part of the frame upload admission.
    internal uint Advance(uint budget)
    {
        RequireRunning();
        if (_uploadSlot < 0) return 0;
        uint uploaded = 0;
        try {
            while (_texels < _edge * _edge && budget - uploaded >= TexelBytes) {
                var x = _texels % _edge;
                var available = (budget - uploaded) / TexelBytes;
                var count = x == 0 && available >= _edge
                    ? System.Math.Min(_edge - _texels / _edge, (int)(available / (uint)_edge)) * _edge
                    : (int)System.Math.Min((uint)(_edge - x), available);
                _write(_uploadSlot, _edge, _texels, _scratch.AsSpan(_texels * 8, count * 8));
                _texels += count; uploaded += (uint)(count * TexelBytes);
            }
            if (_texels == _edge * _edge && budget - uploaded >= sizeof(uint)) {
                ref var entry = ref _entries[_uploadSlot];
                _map(entry.Page, _uploadSlot + 1);
                entry.Published = true; ResidentPages++; Installs++; Revision++;
                _uploadSlot = -1; uploaded += sizeof(uint);
            }
        }
        catch (Exception error) { _failure = error; throw; }
        return uploaded;
    }

    // Return false only when a published mapping does not fit the remaining frame budget.
    // Partial uploads also retire: queued writes may still reference the physical layer.
    internal bool Evict(int page, uint budget, out uint uploaded)
    {
        RequireRunning(); RequirePage(page); uploaded = 0;
        var slot = _allocated[page] - 1;
        if (slot < 0) return true;
        ref var entry = ref _entries[slot];
        if (entry.Published && budget < sizeof(uint)) return false;
        try {
            if (entry.Published) {
                _map(page, 0); uploaded = sizeof(uint);
                entry.Published = false; ResidentPages--; Evictions++; Revision++;
            }
            _allocated[page] = 0;
            if (_uploadSlot == slot) _uploadSlot = -1;
            entry.Retired = true; RetiringSlots++;
            // Capture after withdrawal, including all earlier draws/writes. Failure pins this lease.
            entry.Completion = _afterSubmittedWork()
                ?? throw new InvalidOperationException("Missing lightmap queue completion.");
        }
        catch (Exception error) { _failure = error; throw; }
        return true;
    }

    internal void Reclaim()
    {
        RequireRunning();
        for (var at = 0; at < _entries.Length; at++) {
            ref var entry = ref _entries[at];
            if (!entry.Retired || !entry.Completion!.IsCompleted) continue;
            try {
                if (!entry.Completion.GetAwaiter().GetResult())
                    throw new InvalidOperationException("GPU queue retirement failed; lightmap storage remains quarantined.");
            }
            catch (Exception error) { _failure = error; throw; }
            _ranges.Free(entry.Range); entry = default; RetiringSlots--;
        }
    }

    internal Task StopAsync()
    {
        if (_stop is not null) return _stop;
        _stopping = true;
        return _stop = DrainAsync();
    }

    private async Task DrainAsync()
    {
        // The caller stops all future lightmap draws first; no cancellation abandons this wait.
        var final = _afterSubmittedWork() ?? throw new InvalidOperationException("Missing lightmap queue completion.");
        for (var at = 0; at < _entries.Length; at++) {
            if (_entries[at].Completion is not { } completion) continue;
            if (!await completion.ConfigureAwait(false))
                throw new InvalidOperationException("GPU queue retirement failed; lightmap storage remains quarantined.");
        }
        if (!await final.ConfigureAwait(false))
            throw new InvalidOperationException("GPU queue shutdown failed; lightmap storage remains quarantined.");
        if (_failure is not null) throw new InvalidOperationException("Lightmap queue operations failed.", _failure);
        for (var at = 0; at < _entries.Length; at++) {
            if (_entries[at].Range.Count != 0) _ranges.Free(_entries[at].Range);
            _entries[at] = default;
        }
        Array.Clear(_allocated); _uploadSlot = -1; ResidentPages = RetiringSlots = 0;
        IsStopped = true;
    }

    private void RequireRunning()
    {
        ObjectDisposedException.ThrowIf(_stopping, this);
        if (_failure is not null) throw new InvalidOperationException("Lightmap queue operations failed; slots remain quarantined.", _failure);
    }

    private void RequirePage(int page)
    {
        if ((uint)page >= _allocated.Length) throw new ArgumentOutOfRangeException(nameof(page));
    }
}
