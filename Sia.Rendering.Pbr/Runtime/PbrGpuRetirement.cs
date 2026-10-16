namespace Sia.Engine.Rendering.Pbr;

// Queue completion can arrive anywhere; resource destruction only happens in explicit owner-context Collect/Dispose.
internal sealed class PbrGpuRetirement
{
    private sealed record Entry(object Owner, ulong Bytes, Task<bool> Completion, Action Release);
    private readonly List<Entry> _entries = [];
    private bool _stopping;
    internal ulong Bytes { get { ulong total = 0; foreach (var value in _entries) total = checked(total + value.Bytes); return total; } }
    internal ulong BytesFor(object owner) {
        ulong total = 0; foreach (var value in _entries) if (ReferenceEquals(value.Owner, owner)) total = checked(total + value.Bytes); return total;
    }
    internal bool IsDrained => _stopping && _entries.All(static e => e.Completion.IsCompletedSuccessfully && e.Completion.Result);

    internal void RequireCapacity(object owner)
    {
        if (_stopping) throw new InvalidOperationException("Retirement is draining; no new frame resources may be retired.");
        if (_entries.Count(value => ReferenceEquals(owner, value.Owner)) >= 4)
            throw new InvalidOperationException("Per-view pending resource retirement admission is full.");
    }

    internal void Retire(object owner, ulong bytes, Task<bool> completion, Action release)
    {
        ArgumentNullException.ThrowIfNull(owner); ArgumentNullException.ThrowIfNull(completion); ArgumentNullException.ThrowIfNull(release);
        RequireCapacity(owner); _entries.Add(new(owner, bytes, completion, release));
    }

    internal void Collect()
    {
        for (var at = _entries.Count - 1; at >= 0; at--) {
            var entry = _entries[at];
            if (!entry.Completion.IsCompleted) continue;
            if (!entry.Completion.IsCompletedSuccessfully || !entry.Completion.Result)
                throw new InvalidOperationException("GPU queue retirement failed; its resources remain retained.");
            entry.Release(); _entries.RemoveAt(at);
        }
    }

    internal async Task DrainAsync()
    {
        _stopping = true;
        foreach (var entry in _entries)
            if (!await entry.Completion.ConfigureAwait(false)) throw new InvalidOperationException("GPU retirement failed; resources remain retained.");
        // Never mutate World/entities or invoke Release from this asynchronous continuation.
    }
}
