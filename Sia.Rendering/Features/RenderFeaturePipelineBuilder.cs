namespace Sia.Engine.Rendering;

public sealed class RenderFeaturePipelineBuilder<TContext>
{
    private sealed record Entry(
        RenderFeatureKey Key,
        IRenderFeature Feature,
        HashSet<RenderFeatureKey> RunsAfter,
        HashSet<RenderFeatureKey> RunsBefore,
        long InsertionIndex);

    private readonly List<Entry> _entries = [];
    private readonly Dictionary<RenderFeatureKey, Entry> _entriesByKey = [];
    private long _nextInsertionIndex;

    public RenderFeaturePipelineBuilder<TContext> Add(
        IRenderFeature feature,
        IEnumerable<RenderFeatureKey>? runsAfter = null,
        IEnumerable<RenderFeatureKey>? runsBefore = null)
    {
        ArgumentNullException.ThrowIfNull(feature);
        var key = feature.Key;
        ArgumentException.ThrowIfNullOrWhiteSpace(key.Value, nameof(feature));
        if (_entriesByKey.ContainsKey(key)) {
            throw new InvalidOperationException(
                $"Render feature '{key}' is already registered.");
        }

        var entry = new Entry(
            key,
            feature,
            runsAfter?.ToHashSet() ?? [],
            runsBefore?.ToHashSet() ?? [],
            _nextInsertionIndex);
        var nextInsertionIndex = checked(_nextInsertionIndex + 1);
        _entries.Add(entry);
        _entriesByKey.Add(key, entry);
        _nextInsertionIndex = nextInsertionIndex;
        return this;
    }

    public bool Remove(RenderFeatureKey key)
    {
        if (!_entriesByKey.Remove(key, out var entry)) return false;
        _entries.Remove(entry);
        return true;
    }

    public RenderFeaturePipeline<TContext> Build()
    {
        var outgoing = _entries.ToDictionary(static entry => entry, static _ => new HashSet<Entry>());
        var incomingCounts = _entries.ToDictionary(static entry => entry, static _ => 0);

        foreach (var entry in _entries) {
            foreach (var dependency in entry.RunsAfter) {
                AddEdge(GetRequired(dependency), entry, outgoing, incomingCounts);
            }
            foreach (var successor in entry.RunsBefore) {
                AddEdge(entry, GetRequired(successor), outgoing, incomingCounts);
            }
        }

        var ready = new PriorityQueue<Entry, long>();
        foreach (var entry in _entries) {
            if (incomingCounts[entry] == 0) {
                ready.Enqueue(entry, entry.InsertionIndex);
            }
        }

        var ordered = new List<IRenderFeature>(_entries.Count);
        while (ready.TryDequeue(out var entry, out _)) {
            ordered.Add(entry.Feature);
            foreach (var successor in outgoing[entry]) {
                incomingCounts[successor]--;
                if (incomingCounts[successor] == 0) {
                    ready.Enqueue(successor, successor.InsertionIndex);
                }
            }
        }

        if (ordered.Count != _entries.Count) {
            var cyclicKeys = incomingCounts
                .Where(static pair => pair.Value != 0)
                .OrderBy(static pair => pair.Key.InsertionIndex)
                .Select(static pair => pair.Key.Key);
            throw new InvalidOperationException(
                $"Render feature ordering contains a cycle: {string.Join(", ", cyclicKeys)}.");
        }

        return new([.. ordered]);
    }

    private Entry GetRequired(RenderFeatureKey key) =>
        _entriesByKey.TryGetValue(key, out var entry)
            ? entry
            : throw new InvalidOperationException(
                $"Render feature ordering references unregistered feature '{key}'.");

    private static void AddEdge(
        Entry from,
        Entry to,
        Dictionary<Entry, HashSet<Entry>> outgoing,
        Dictionary<Entry, int> incomingCounts)
    {
        if (from == to) {
            throw new InvalidOperationException(
                $"Render feature '{from.Key}' cannot be ordered relative to itself.");
        }
        if (outgoing[from].Add(to)) {
            incomingCounts[to]++;
        }
    }
}
