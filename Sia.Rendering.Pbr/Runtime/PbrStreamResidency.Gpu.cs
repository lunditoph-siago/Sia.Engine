using System.Runtime.InteropServices;
using Sia;
using Sia.Math;
using Sia.WebGPU;

namespace Sia.Engine.Rendering.Pbr;

internal sealed partial class PbrStreamResidency
{
    private Key[] _gpuKeys = [];
    private Dictionary<Key, int>? _gpuSlots;
    private readonly HashSet<int> _gpuDirty = [];

    internal ReadOnlySpan<Aabb> InstanceBounds => _instanceBounds;
    internal PbrSceneStream Source => _source;
    internal PbrGpuHierarchy? Hierarchy { get; private set; }

    internal PbrGpuHierarchy CreateHierarchy(in GpuFrame frame)
    {
        var slots = new Dictionary<Key, int>();
        var keys = new List<Key>();
        var nodeCount = 0;
        var partCount = 0;
        var rootCount = 0;
        foreach (var instance in _source.Instances.Span) {
            var tree = _source.Hierarchies[instance.AssetIndex];
            nodeCount = checked(nodeCount + tree.Nodes.Length);
            rootCount = checked(rootCount + tree.Roots);
            foreach (var node in tree.Nodes) partCount = checked(partCount + node.Pages.Length);
        }
        if (((ulong)nodeCount * 64) + (((ulong)partCount + (ulong)rootCount) * 16) > _settings.HierarchyBytes)
            throw new ArgumentException("GPU hierarchy metadata exceeds its explicit allocation budget.");
        var nodes = new List<PbrGpuHierarchy.Node>(nodeCount);
        var parts = new List<uint4>(checked(partCount + rootCount));
        var roots = new List<uint>(rootCount);
        var capacities = _source.Hierarchies.Select(PbrGpuHierarchy.MaximumCutTriangles).ToArray();
        ulong single = 0, twice = 0;
        for (var instance = 0; instance < _source.Instances.Length; instance++) {
            var info = _source.Instances.Span[instance];
            var tree = _source.Hierarchies[info.AssetIndex];
            var first = nodes.Count;
            var side = _source.Bootstrap.Materials.Span[info.MaterialIndex].DoubleSided ? 1u : 0u;
            if (side == 0) single = checked(single + capacities[info.AssetIndex]);
            else twice = checked(twice + capacities[info.AssetIndex]);
            for (var n = 0; n < tree.Nodes.Length; n++) {
                var node = tree.Nodes[n];
                var bounds = _bounds[instance] is { } cached ? cached[n]
                    : BoundsTransform.Apply(PbrSceneStream.NodeBounds(node), info.Transform);
                var at = parts.Count;
                foreach (var part in node.Pages) {
                    var key = PageKey(instance, part.Id);
                    if (!slots.TryGetValue(key, out var slot)) {
                        slot = keys.Count;
                        slots.Add(key, slot);
                        keys.Add(key);
                    }
                    parts.Add(new((uint)slot, (uint)part.First, (uint)part.Count, 0));
                }
                nodes.Add(new(new(bounds.Min, node.ChildCount == 0 ? 0 : SafeError(node.Error * _norms[instance])), new(bounds.Max, 0),
                    new((uint)(first + node.Children), (uint)node.ChildCount, (uint)at, (uint)node.Pages.Length),
                    new(node.Parent < 0 ? uint.MaxValue : (uint)(first + node.Parent), side, 0, 0)));
                if (n < tree.Roots) roots.Add((uint)(first + n));
                if (((ulong)nodes.Count * 64) + ((ulong)(parts.Count + roots.Count) * 16) + ((ulong)keys.Count * 16) > _settings.HierarchyBytes)
                    throw new ArgumentException("GPU hierarchy metadata exceeds its explicit allocation budget; use CPU traversal or recook cheaper hierarchy roots.");
            }
        }
        var rootBase = parts.Count;
        foreach (var root in roots) parts.Add(new(root, 0, 0, 0));
        var mapping = new uint4[keys.Count];
        for (var i = 0; i < keys.Count; i++) {
            if (!_resident.TryGetValue(keys[i], out var page)) continue;
            mapping[i] = new(page.Allocation.Triangles.Offset, page.Allocation.Triangles.Count, page.Root ? 1u : 0u, 1);
        }
        if (single > uint.MaxValue / 3 || twice > uint.MaxValue / 3 || single + twice > uint.MaxValue / 3)
            throw new NotSupportedException("GPU logical cut exceeds indirect vertex addressing capacity.");
        var hierarchy = new PbrGpuHierarchy(frame, _settings.HierarchyBytes, CollectionsMarshal.AsSpan(nodes),
            CollectionsMarshal.AsSpan(parts), mapping, (uint)rootBase, (uint)roots.Count,
            checked((uint)single), checked((uint)twice));
        _gpuKeys = [.. keys];
        _gpuSlots = slots;
        return Hierarchy = hierarchy;
    }

    internal void RefreshGpuResidency()
    {
        if (Hierarchy is null) return;
        foreach (var slot in _gpuDirty) {
            var data = _resident.TryGetValue(_gpuKeys[slot], out var page)
                ? new uint4(page.Allocation.Triangles.Offset, page.Allocation.Triangles.Count, page.Root ? 1u : 0u, 1) : default;
            Hierarchy.Publish(slot, data);
        }
        _gpuDirty.Clear();
    }

    internal void ApplyGpuFeedback(ReadOnlySpan<uint> words, ReadOnlySpan<int> active)
    {
        if (words.Length != _gpuKeys.Length * 2) throw new InvalidDataException("GPU feedback length mismatch.");
        foreach (var i in active) {
            if (words[(i * 2) + 1] != 0) Touch(_gpuKeys[i]);
            if (words[i * 2] == 0) continue;
            var priority = BitConverter.UInt32BitsToSingle(words[i * 2]);
            if (!float.IsFinite(priority) || priority <= 0)
                throw new InvalidDataException("GPU feedback priority is invalid.");
            Demand(_gpuKeys[i], priority);
        }
    }

    private static float SafeError(float error) => float.IsFinite(error) ? error : 1.0e30f;
}
