using Sia.Math;

namespace Sia.Engine.Rendering;

/// <summary>Fixed scene-slot bounds with logarithmic updates and constant-time aggregate reads.</summary>
public sealed class SceneBoundsTree
{
    private readonly Aabb?[] _nodes;
    private readonly int _leaves;
    public int Capacity { get; }
    public Aabb? Bounds => _nodes[1];

    public SceneBoundsTree(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        Capacity = capacity;
        _leaves = 1;
        while (_leaves < capacity) _leaves = checked(_leaves * 2);
        _nodes = new Aabb?[checked(_leaves * 2)];
    }

    public void Set(int slot, Aabb? bounds)
    {
        if ((uint)slot >= (uint)Capacity) throw new ArgumentOutOfRangeException(nameof(slot));
        var node = _leaves + slot;
        if (Nullable.Equals(_nodes[node], bounds)) return;
        _nodes[node] = bounds;
        while ((node /= 2) != 0) {
            var left = _nodes[node * 2]; var right = _nodes[node * 2 + 1];
            _nodes[node] = left is { } a && right is { } b ? Aabb.Union(a, b) : left ?? right;
        }
    }
}
