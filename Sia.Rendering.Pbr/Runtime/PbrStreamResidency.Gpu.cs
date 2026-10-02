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

    internal PbrGpuHierarchy CreateHierarchy(in GpuFrame frame, Entity instances)
    {
        var slots = new Dictionary<Key, int>();
        var keys = new List<Key>();
        var nodeCount = 0;
        var partCount = 0;
        var rootCount = 0;
        var assets = new HashSet<int>();
        foreach (var instance in _source.Instances.Span) {
            var tree = _source.Hierarchies[instance.AssetIndex];
            rootCount = checked(rootCount + tree.Roots);
            if (!assets.Add(instance.AssetIndex)) continue;
            nodeCount = checked(nodeCount + tree.Nodes.Length);
            foreach (var node in tree.Nodes) partCount = checked(partCount + node.Pages.Length);
        }
        if (((ulong)nodeCount * 64) + (((ulong)partCount + (ulong)rootCount) * 16) > _settings.HierarchyBytes)
            throw new ArgumentException("GPU hierarchy metadata exceeds its explicit allocation budget.");
        var nodes = new List<PbrGpuHierarchy.Node>(nodeCount);
        var parts = new List<uint4>(checked(partCount + rootCount));
        var roots = new List<uint4>(rootCount);
        var offsets = new Dictionary<int, int>(assets.Count);
        var capacities = _source.Hierarchies.Select(PbrGpuHierarchy.MaximumCut).ToArray();
        ulong single = 0, twice = 0, selectedNodes = 0, triangles = 0;
        for (var instance = 0; instance < _source.Instances.Length; instance++) {
            var info = _source.Instances.Span[instance];
            var tree = _source.Hierarchies[info.AssetIndex];
            var side = _source.Bootstrap.Materials.Span[info.MaterialIndex].DoubleSided ? 1u : 0u;
            var capacity = capacities[info.AssetIndex];
            if (side == 0) single = checked(single + capacity.Records);
            else twice = checked(twice + capacity.Records);
            triangles = checked(triangles + capacity.Triangles);
            selectedNodes = checked(selectedNodes + capacity.Nodes);
            if (!offsets.TryGetValue(info.AssetIndex, out var first)) {
                first = nodes.Count;
                offsets.Add(info.AssetIndex, first);
                for (var n = 0; n < tree.Nodes.Length; n++) {
                    var node = tree.Nodes[n];
                    var bounds = PbrSceneStream.NodeBounds(node);
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
                    var radius = math.length((bounds.Max - bounds.Min) * .5f);
                    nodes.Add(new(new(bounds.Min, node.ChildCount == 0 ? 0 : SafeError(node.Error)), new(bounds.Max, radius),
                        new((uint)(first + node.Children), (uint)node.ChildCount, (uint)at, (uint)node.Pages.Length),
                        new(node.Parent < 0 ? uint.MaxValue : (uint)(first + node.Parent),
                            0, 0, 0)));
                    if (((ulong)nodes.Count * 64) + ((ulong)(parts.Count + rootCount) * 16) + ((ulong)keys.Count * 16) > _settings.HierarchyBytes)
                        throw new ArgumentException("GPU hierarchy metadata exceeds its explicit allocation budget; use CPU traversal or recook cheaper hierarchy roots.");
                }
            }
            for (var root = 0; root < tree.Roots; root++)
                roots.Add(new((uint)(first + root), (uint)instance, side, 0));
        }
        var rootBase = parts.Count;
        parts.AddRange(roots);
        var mapping = new uint4[keys.Count];
        for (var i = 0; i < keys.Count; i++) {
            if (!_resident.TryGetValue(keys[i], out var page)) continue;
            mapping[i] = new(page.Allocation.Triangles.Offset, page.Allocation.Triangles.Count, page.Root ? 1u : 0u, 1);
        }
        if (single + twice > uint.MaxValue / (3ul * PbrGpuHierarchy.WorkBlockTriangles)
            || triangles > uint.MaxValue || single + twice + selectedNodes * 3 > uint.MaxValue)
            throw new NotSupportedException("GPU logical cut exceeds indirect vertex addressing capacity.");
        var hierarchy = new PbrGpuHierarchy(frame, _settings.HierarchyBytes, CollectionsMarshal.AsSpan(nodes),
            CollectionsMarshal.AsSpan(parts), mapping, (uint)rootBase, (uint)roots.Count,
            checked((uint)single), checked((uint)twice), checked((uint)selectedNodes), instances);
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
