namespace Sia.Engine.Rendering;

public sealed class RenderPhaseCollection
{
    private readonly Dictionary<RenderPhaseKey, IRenderPhase> _phases = [];

    public int Count => _phases.Count;

    public RenderPhase<TItem> GetOrAdd<TItem>(
        RenderPhaseKey key,
        IComparer<TItem>? comparer = null)
    {
        if (!_phases.TryGetValue(key, out var phase)) {
            var created = new RenderPhase<TItem>(comparer);
            _phases.Add(key, created);
            return created;
        }
        if (phase is RenderPhase<TItem> typed) {
            return typed;
        }

        throw new InvalidOperationException(
            $"Render phase '{key}' contains '{phase.ItemType}', not '{typeof(TItem)}'.");
    }

    public RenderPhase<TItem> GetRequired<TItem>(RenderPhaseKey key) =>
        _phases.TryGetValue(key, out var phase) && phase is RenderPhase<TItem> typed
            ? typed
            : throw new KeyNotFoundException(
                $"Render phase '{key}' with item type '{typeof(TItem)}' is not available.");

    public bool Remove(RenderPhaseKey key) => _phases.Remove(key);

    public void Clear()
    {
        foreach (var phase in _phases.Values) {
            phase.Clear();
        }
    }
}
