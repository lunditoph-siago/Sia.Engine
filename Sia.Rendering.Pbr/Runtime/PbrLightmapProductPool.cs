namespace Sia.Engine.Rendering.Pbr;

internal delegate void PbrLightmapMetadataWrite(int firstTexel, ReadOnlySpan<uint> words);
internal delegate bool PbrLightmapPageWithdrawal(StreamRange pages, uint budget, out uint uploaded);

// Rendering owns metadata/address leases. Typed asset acquisition is supplied by
// the caller, and its inverse runs synchronously in Reclaim/Dispose on that owner.
// There is no asset IO, backend or hidden World mutation in this pool.
internal sealed class PbrLightmapProductPool : IDisposable
{
    private sealed class Entry(int product, PbrLightmapData data, StreamRange metadata, StreamRange pages,
        uint[] words, Action release)
    {
        internal readonly int Product = product;
        internal readonly PbrLightmapData Data = data;
        internal readonly StreamRange Metadata = metadata, Pages = pages;
        internal readonly Action Release = release;
        internal uint[]? Words = words;
        internal int Uploaded;
        internal bool Published, Withdrawing, Retired, Released;
        internal Task<bool>? Completion;
        internal ulong CpuBytes => Data.PayloadBytes + (ulong)(Words?.Length ?? 0) * sizeof(uint);
    }

    private readonly PbrLightmapSetData _set;
    private readonly StreamRangePool _metadata, _pages;
    private readonly Entry?[] _current;
    private readonly List<Entry> _entries = [];
    private readonly int _firstMetadataTexel;
    private readonly ulong _cpuBudget;
    private readonly PbrLightmapMetadataWrite _write;
    private readonly Action<int, uint> _map;
    private readonly PbrLightmapPageWithdrawal _withdraw;
    private readonly Func<Task<bool>> _afterSubmittedWork;
    private Entry? _upload;
    private Exception? _failure;
    private Task? _stop;
    private bool _stopping, _disposed;
    internal bool IsStopped { get; private set; }
    internal int ActiveProduct => _upload?.Product ?? -1;
    internal uint OccupiedMetadataTexels => _metadata.Used;
    internal uint OccupiedPageAddresses => _pages.Used;
    internal uint PageAddressCapacity => _pages.Capacity;
    internal int ResidentProducts { get; private set; }
    internal int RetiringProducts => _entries.Count(entry => entry.Withdrawing || entry.Retired);
    internal ulong CpuBytes => _entries.Aggregate(0ul, (total, entry) => checked(total + entry.CpuBytes));
    internal long Installs { get; private set; }
    internal long Evictions { get; private set; }
    internal ulong Revision { get; private set; }

    internal PbrLightmapProductPool(PbrLightmapSetData set, int firstMetadataTexel, uint metadataTexels,
        uint pageAddresses, ulong cpuBytes, PbrLightmapMetadataWrite write, Action<int, uint> map,
        PbrLightmapPageWithdrawal withdraw, Func<Task<bool>> afterSubmittedWork)
    {
        ArgumentNullException.ThrowIfNull(set); ArgumentNullException.ThrowIfNull(write); ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(withdraw); ArgumentNullException.ThrowIfNull(afterSubmittedWork);
        if (firstMetadataTexel < 1 || metadataTexels == 0 || metadataTexels > int.MaxValue / 16
            || firstMetadataTexel > int.MaxValue - metadataTexels || pageAddresses is < 1 or > PbrLightmapData.MaximumPages || cpuBytes == 0)
            throw new ArgumentOutOfRangeException(nameof(metadataTexels));
        _set = set; _firstMetadataTexel = firstMetadataTexel; _cpuBudget = cpuBytes;
        _metadata = new(metadataTexels); _pages = new(pageAddresses); _current = new Entry?[set.Products.Length];
        _write = write; _map = map; _withdraw = withdraw; _afterSubmittedWork = afterSubmittedWork;
    }

    internal static ulong RequiredCpuBytes(PbrLightmapData data)
    {
        ArgumentNullException.ThrowIfNull(data); var scratch = 0;
        foreach (var receiver in data.Receivers) scratch = System.Math.Max(scratch, receiver.ChartCount);
        return checked(data.PayloadBytes + PbrLightmapMetadata.RequiredBytes(data, 1) + (ulong)scratch * sizeof(int));
    }

    internal bool HasAllocation(int product) { RequireProduct(product); return _current[product] is not null; }
    internal bool Matches(PbrLightmapSetData set) => _set.SameRevision(set);

    internal bool CanFit(int product)
    {
        RequireProduct(product); var counts = _set.ProductCounts(product);
        var bytes = PbrLightmapMetadata.RequiredBytes(counts.Receivers, counts.Charts, 1);
        var scratch = 0;
        foreach (var receiver in _set.Receivers)
            if (receiver.Product == product) scratch = System.Math.Max(scratch, receiver.Receiver.ChartCount);
        var cpu = checked(PbrLightmapData.RequiredBytes(counts.Receivers, counts.Charts, counts.Pages, _set.BakeSettings) + bytes + (ulong)scratch * 4);
        return bytes / 16 <= _metadata.Capacity && counts.Pages <= _pages.Capacity && cpu <= _cpuBudget;
    }

    internal (PbrLightmapData Data, StreamRange Metadata, StreamRange Pages)? Resident(int product)
    {
        RequireProduct(product);
        return _current[product] is { Published: true } entry ? (entry.Data, entry.Metadata, entry.Pages) : null;
    }

    // Acquisition is invoked only after pure metadata preparation and complete
    // admission. Its returned inverse belongs to this pool only on success.
    internal bool BeginUpload(int product, PbrLightmapData data,
        Func<(PbrLightmapData Data, Action Release)> acquire, CancellationToken token = default)
    {
        RequireRunning(); RequireProduct(product); ArgumentNullException.ThrowIfNull(acquire);
        token.ThrowIfCancellationRequested(); _set.ValidateProduct(product, data, token);
        if (_current[product] is not null || _upload is not null) return false;
        var required = RequiredCpuBytes(data);
        if (required > _cpuBudget || CpuBytes > _cpuBudget - required) return false;
        var texels = checked((uint)(PbrLightmapMetadata.RequiredBytes(data, 1) / 16));
        if (!_metadata.TryAllocate(texels, out var metadata)) return false;
        if (!_pages.TryAllocate((uint)data.Pages.Length, out var pages)) { _metadata.Free(metadata); return false; }
        Entry entry; Action? release = null;
        try {
            var table = new PbrLightmapMetadata(data, [-1], required - data.PayloadBytes, token);
            var receiverStart = (int)table.Words[5];
            for (var at = 0; at < data.Receivers.Length; at++) {
                token.ThrowIfCancellationRequested(); var address = (receiverStart + at * 5) * 4 + 3;
                table.Words[address] = checked(table.Words[address] + pages.Offset);
            }
            var acquired = acquire(); release = acquired.Release;
            if (release is null) { _failure = new InvalidOperationException("Product acquisition must return an inverse operation."); throw _failure; }
            if (!data.SameRevision(acquired.Data)) throw new InvalidDataException("Acquired product differs from the verified prepared revision.");
            entry = new(product, acquired.Data, metadata, pages, table.Words, release);
            _entries.Add(entry);
        }
        catch (Exception error) {
            _pages.Free(pages); _metadata.Free(metadata);
            try { release?.Invoke(); }
            catch (Exception inverse) { _failure = new AggregateException("Product preparation and acquisition rollback failed.", error, inverse); throw _failure; }
            throw;
        }
        _current[product] = _upload = entry;
        return true;
    }

    internal uint Advance(uint budget)
    {
        RequireRunning();
        if (_upload is not { } entry) return 0;
        uint uploaded = 0;
        try {
            var words = entry.Words!;
            var remainingTexels = (words.Length - entry.Uploaded) / 4;
            var count = (int)System.Math.Min((uint)remainingTexels, budget / 16);
            if (count != 0) {
                _write(checked(_firstMetadataTexel + (int)entry.Metadata.Offset + entry.Uploaded / 4),
                    words.AsSpan(entry.Uploaded, count * 4));
                entry.Uploaded += count * 4; uploaded = (uint)count * 16;
            }
            var mappingBytes = MappingBytes(entry.Product);
            if (entry.Uploaded == words.Length && budget - uploaded >= mappingBytes) {
                _map(entry.Product, checked((uint)_firstMetadataTexel + entry.Metadata.Offset));
                entry.Published = true; ResidentProducts++; Installs++; Revision++;
                entry.Words = null; _upload = null; uploaded += mappingBytes;
            }
        }
        catch (Exception error) { _failure = error; throw; }
        return uploaded;
    }

    // Withdrawal can span frames. Root pointers are zeroed before any page
    // mapping/physical lease is withdrawn, and the fence is captured last.
    internal bool Evict(int product, uint budget, out uint uploaded)
    {
        RequireRunning(); RequireProduct(product); uploaded = 0;
        if (_current[product] is not { } entry) return true;
        if (entry.Published && budget < MappingBytes(product)) return false;
        try {
            if (entry.Published) {
                _map(product, 0); uploaded += MappingBytes(product);
                entry.Published = false; entry.Withdrawing = true;
                ResidentProducts--; Evictions++; Revision++;
            }
            if (_upload == entry) _upload = null;
            if (entry.Withdrawing) {
                var complete = _withdraw(entry.Pages, budget - uploaded, out var bytes);
                if (bytes > budget - uploaded) throw new InvalidOperationException("Page withdrawal exceeded its frame admission.");
                uploaded += bytes;
                if (!complete) return false;
            }
            entry.Withdrawing = false; entry.Retired = true;
            entry.Completion = _afterSubmittedWork() ?? throw new InvalidOperationException("Missing product queue completion.");
            _current[product] = null;
        }
        catch (Exception error) { _failure = error; throw; }
        return true;
    }

    internal void Reclaim()
    {
        RequireRunning();
        for (var at = _entries.Count - 1; at >= 0; at--) {
            var entry = _entries[at];
            if (!entry.Retired || !entry.Completion!.IsCompleted) continue;
            try {
                if (!entry.Completion.GetAwaiter().GetResult()) throw new InvalidOperationException("Product queue retirement failed.");
                Release(entry); _entries.RemoveAt(at);
            }
            catch (Exception error) { _failure = error; throw; }
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
        var final = _afterSubmittedWork() ?? throw new InvalidOperationException("Missing product queue completion.");
        var completions = _entries.Where(entry => entry.Completion is not null).Select(entry => entry.Completion!).Append(final).ToArray();
        var completed = await Task.WhenAll(completions).ConfigureAwait(false);
        if (completed.Any(result => !result)) throw new InvalidOperationException("Product queue shutdown/retirement failed.");
        if (_failure is not null) throw new InvalidOperationException("Product operations failed; leases remain quarantined.", _failure);
        // Asset inverse callbacks run only in the synchronous owner-side Dispose.
        IsStopped = true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        if (!IsStopped || _failure is not null) throw new InvalidOperationException("Stop and drain products before owner-side disposal.");
        try {
            foreach (var entry in _entries) Release(entry);
            _entries.Clear(); Array.Clear(_current); _upload = null; ResidentProducts = 0; _disposed = true;
        }
        catch (Exception error) { _failure = error; throw; }
    }

    private void Release(Entry entry)
    {
        if (entry.Released) throw new InvalidOperationException("Product inverse operation has already been attempted.");
        entry.Released = true; entry.Release();
        _pages.Free(entry.Pages); _metadata.Free(entry.Metadata);
    }

    private uint MappingBytes(int product) => checked((uint)_set.ProductCounts(product).Receivers * 16);
    private void RequireProduct(int product) { if ((uint)product >= _current.Length) throw new ArgumentOutOfRangeException(nameof(product)); }
    private void RequireRunning()
    {
        ObjectDisposedException.ThrowIf(_stopping, this);
        if (_failure is not null) throw new InvalidOperationException("Product operations failed; leases remain quarantined.", _failure);
    }
}
