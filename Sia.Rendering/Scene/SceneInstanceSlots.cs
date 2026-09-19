using Sia;

namespace Sia.Engine.Rendering;

// Slot identity belongs to the scene, independently of ECS query/archetype order.
// Vacant slots have zero roots on the GPU; surviving instances never move.
public sealed class SceneInstanceSlots
{
    private readonly Dictionary<Entity, int> _slots = [];
    private readonly HashSet<Entity> _seen = [];
    private readonly Stack<int> _free = [];
    private readonly Entity?[] _owners;
    public ReadOnlySpan<Entity?> Owners => _owners;
    public int Length { get; private set; }
    public int Count => _slots.Count;

    public SceneInstanceSlots(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        _owners = new Entity?[capacity];
    }

    public bool Synchronize(IReadOnlyList<Entity> entities)
    {
        if (entities.Count > Owners.Length) throw new InvalidOperationException("The scene instance count exceeds the reserved visibility capacity.");
        _seen.Clear();
        foreach (var entity in entities) {
            if (!_seen.Add(entity)) throw new ArgumentException("Duplicate scene instance.", nameof(entities));
        }
        var changed = false;
        for (var i = 0; i < Length; i++) {
            if (Owners[i] is not { } owner || _seen.Contains(owner)) continue;
            _slots.Remove(owner); _owners[i] = null; _free.Push(i); changed = true;
        }
        foreach (var entity in entities) {
            if (_slots.ContainsKey(entity)) continue;
            var slot = _free.TryPop(out var free) ? free : Length++;
            _slots.Add(entity, slot); _owners[slot] = entity; changed = true;
        }
        return changed;
    }
}
