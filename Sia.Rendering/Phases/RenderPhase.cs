namespace Sia.Engine.Rendering;

public sealed class RenderPhase<TItem>(IComparer<TItem>? comparer = null) : IRenderPhase
{
    private readonly List<TItem> _items = [];
    private readonly IComparer<TItem>? _comparer = comparer;

    public Type ItemType => typeof(TItem);

    public int Count => _items.Count;

    public IReadOnlyList<TItem> Items => _items;

    public void Add(TItem item) => _items.Add(item);

    public void AddRange(IEnumerable<TItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        _items.AddRange(items);
    }

    public void Sort()
    {
        if (_comparer is null) {
            throw new InvalidOperationException(
                $"Render phase item '{typeof(TItem)}' has no comparer.");
        }
        _items.Sort(_comparer);
    }

    public IReadOnlyList<RenderPhaseBatch> BuildBatches(
        Func<TItem, TItem, bool> canBatch)
    {
        ArgumentNullException.ThrowIfNull(canBatch);
        if (_items.Count == 0) {
            return [];
        }

        var batches = new List<RenderPhaseBatch>();
        var start = 0;
        for (var index = 1; index < _items.Count; index++) {
            if (canBatch(_items[index - 1], _items[index])) {
                continue;
            }

            batches.Add(new RenderPhaseBatch(start, index - start));
            start = index;
        }
        batches.Add(new RenderPhaseBatch(start, _items.Count - start));
        return batches;
    }

    public void Clear() => _items.Clear();
}
