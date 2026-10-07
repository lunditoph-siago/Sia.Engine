namespace Sia.Engine.Rendering;

public sealed class RenderFeaturePipelineBuilder<TContext>
{
    private sealed record Entry(
        IRenderFeature Feature,
        RenderFeatureKey Key,
        HashSet<RenderFeatureKey> RunsAfter,
        HashSet<RenderFeatureKey> RunsBefore);

    private readonly List<Entry> _entries = [];
    private readonly Dictionary<RenderFeatureKey, Entry> _entriesByKey = [];

    public RenderFeaturePipelineBuilder<TContext> Add(
        IRenderFeature feature,
        IEnumerable<RenderFeatureKey>? runsAfter = null,
        IEnumerable<RenderFeatureKey>? runsBefore = null)
    {
        ArgumentNullException.ThrowIfNull(feature);
        var key = feature.Key;
        if (_entriesByKey.ContainsKey(key)) {
            throw new InvalidOperationException(
                $"Render feature '{key}' is already registered.");
        }

        var entry = new Entry(
            feature,
            key,
            runsAfter?.ToHashSet() ?? [],
            runsBefore?.ToHashSet() ?? []);
        _entries.Add(entry);
        _entriesByKey.Add(key, entry);
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
        // Current list positions remain unique after removal and re-registration.
        var indices = _entries.Select((entry, index) => (entry.Key, Index: index))
            .ToDictionary(static pair => pair.Key, static pair => pair.Index);
        var outgoing = new HashSet<int>[_entries.Count];
        var incoming = new int[_entries.Count];
        for (var i = 0; i < outgoing.Length; i++) outgoing[i] = [];

        for (var i = 0; i < _entries.Count; i++) {
            var entry = _entries[i];
            foreach (var dependency in entry.RunsAfter) {
                AddEdge(GetRequired(dependency), i);
            }
            foreach (var successor in entry.RunsBefore) {
                AddEdge(i, GetRequired(successor));
            }
        }

        var ready = new PriorityQueue<int, int>();
        for (var i = 0; i < incoming.Length; i++) {
            if (incoming[i] == 0) ready.Enqueue(i, i);
        }

        var ordered = new List<IRenderFeature>(_entries.Count);
        while (ready.TryDequeue(out var index, out _)) {
            ordered.Add(_entries[index].Feature);
            foreach (var successor in outgoing[index]) {
                if (--incoming[successor] == 0) {
                    ready.Enqueue(successor, successor);
                }
            }
        }

        if (ordered.Count != _entries.Count) {
            var cyclicKeys = Enumerable.Range(0, incoming.Length)
                .Where(i => incoming[i] != 0).Select(i => _entries[i].Key);
            throw new InvalidOperationException(
                $"Render feature ordering contains a cycle: {string.Join(", ", cyclicKeys)}.");
        }

        return new([.. ordered]);

        int GetRequired(RenderFeatureKey key) => indices.TryGetValue(key, out var index)
            ? index : throw new InvalidOperationException(
                $"Render feature ordering references unregistered feature '{key}'.");

        void AddEdge(int from, int to)
        {
            if (from == to) throw new InvalidOperationException(
                $"Render feature '{_entries[from].Key}' cannot be ordered relative to itself.");
            if (outgoing[from].Add(to)) incoming[to]++;
        }
    }
}
