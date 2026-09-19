using Sia;

namespace Sia.Engine.Rendering.Pbr;

// Slot identity belongs to the scene, independently of ECS query/archetype order.
// Vacant slots have zero roots on the GPU; surviving instances never move.
internal sealed class VisibilityInstanceSlots(int capacity)
{
    private readonly Dictionary<Entity, int> _slots = [];
    private readonly HashSet<Entity> _seen = [];
    private readonly Stack<int> _free = [];
    public Entity?[] Owners { get; } = new Entity?[capacity];
    public int Length { get; private set; }
    public int Count => _slots.Count;

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
            _slots.Remove(owner); Owners[i] = null; _free.Push(i); changed = true;
        }
        foreach (var entity in entities) {
            if (_slots.ContainsKey(entity)) continue;
            var slot = _free.TryPop(out var free) ? free : Length++;
            _slots.Add(entity, slot); Owners[slot] = entity; changed = true;
        }
        return changed;
    }
}
