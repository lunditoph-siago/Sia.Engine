namespace Sia.Engine.Rendering;

public readonly record struct StreamRange(uint Offset, uint Count, ulong Generation);

public sealed class StreamRangePool
{
    private static long s_Generation;

    private readonly List<(uint Offset, uint Count)> _free;
    private readonly Dictionary<ulong, StreamRange> _live = [];

    public uint Capacity { get; }
    public uint Used { get; private set; }

    /// <summary>Largest currently allocatable range; querying does not reserve or move storage.</summary>
    public uint MaximumFreeRange {
        get {
            uint maximum = 0;
            foreach (var range in _free) maximum = System.Math.Max(maximum, range.Count);
            return maximum;
        }
    }

    public StreamRangePool(uint capacity)
    {
        ArgumentOutOfRangeException.ThrowIfZero(capacity);
        Capacity = capacity;
        _free = [(0, capacity)];
    }

    public bool TryAllocate(uint count, out StreamRange range)
    {
        range = default;
        ArgumentOutOfRangeException.ThrowIfZero(count);
        for (var i = 0; i < _free.Count; i++) {
            var (Offset, Count) = _free[i];
            if (Count < count) continue;
            range = new(Offset, count, checked((ulong)Interlocked.Increment(ref s_Generation)));
            if (Count == count) _free.RemoveAt(i);
            else _free[i] = (Offset + count, Count - count);
            _live.Add(range.Generation, range);
            Used += count;
            return true;
        }
        return false;
    }

    public void Free(StreamRange range)
    {
        if (!Contains(range)) throw new InvalidOperationException("Stale or foreign geometry range.");
        _live.Remove(range.Generation);
        Used -= range.Count;
        var at = _free.FindIndex(f => f.Offset > range.Offset);
        if (at < 0) at = _free.Count;
        _free.Insert(at, (range.Offset, range.Count));
        for (var i = System.Math.Max(0, at - 1); i + 1 < _free.Count;) {
            var (Offset, Count) = _free[i];
            var b = _free[i + 1];
            if (Offset + Count == b.Offset) {
                _free[i] = (Offset, Count + b.Count);
                _free.RemoveAt(i + 1);
            }
            else i++;
        }
    }

    public bool Contains(StreamRange range)
        => _live.TryGetValue(range.Generation, out var live) && live == range;
}
