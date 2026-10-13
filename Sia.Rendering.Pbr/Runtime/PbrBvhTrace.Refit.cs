namespace Sia.Engine.Rendering.Pbr;

public sealed partial class PbrBvhTrace
{
    /// <summary>
    /// Returns an immutable runtime snapshot with refitted instance acceleration. Shares the authored
    /// scene, local BVHs and topology/order; never recooks, sorts, acquires or mutates asset data.
    /// Updates are absolute transforms by authored ID and are validated before publication.
    /// Refit preserves topology; rebuild explicitly if sustained motion degrades traversal quality.
    /// </summary>
    public PbrBvhTrace WithTransforms(ReadOnlySpan<PbrInstanceTransform> updates,
        ulong maximumBytes = 128ul * 1024 * 1024, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (PayloadBytes > maximumBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes), "Instance acceleration exceeds its array budget.");
        if (updates.Length > _instances.Length) throw new ArgumentException("Too many instance updates.", nameof(updates));
        if (updates.IsEmpty) return this;
        var pending = new Dictionary<Guid, Sia.Math.float4x4>(updates.Length);
        foreach (var update in updates) {
            cancellationToken.ThrowIfCancellationRequested();
            if (update.Id == Guid.Empty || !PbrSceneData.ValidTransform(update.Transform) || !pending.TryAdd(update.Id, update.Transform))
                throw new ArgumentException("Updates require unique authored IDs and valid affine transforms.", nameof(updates));
        }
        var instances = (InstanceData[])_instances.Clone(); var matched = 0;
        for (var i = 0; i < instances.Length; i++) {
            if ((i & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (!pending.TryGetValue(Scene.Instances[i].Id, out var transform)) continue;
            instances[i] = CreateInstance(Scene, i, transform); matched++;
        }
        if (matched != pending.Count) throw new ArgumentException("An updated authored instance is absent from this trace.", nameof(updates));

        var nodes = new Node[_nodes.Length];
        for (var i = nodes.Length - 1; i >= 0; i--) {
            if ((i & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            var node = _nodes[i];
            if (node.Count != 0) {
                var bounds = instances[_order[node.First]].Bounds;
                for (var at = node.First + 1; at < node.First + node.Count; at++)
                    bounds = Sia.Math.Aabb.Union(bounds, instances[_order[at]].Bounds);
                nodes[i] = node with { Bounds = bounds };
            }
            else nodes[i] = node with { Bounds = Sia.Math.Aabb.Union(nodes[i + 1].Bounds, nodes[_nodes[i + 1].Escape].Bounds) };
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(Scene, instances, _order, nodes, PayloadBytes);
    }
}
