using Sia;
using Sia.Engine.Mesh;
using Sia.Math;
using Sia.WebGPU;

namespace Sia.Engine.Rendering.Pbr;

public sealed partial class VisibilityPbrFeature
{
    private static readonly IEntityMatcher s_InstanceMatcher = Matchers.Of<VisibilityInstance>();
    private World? _instanceWorld;
    private AssetRoots[] _instanceAssets = [];
    private InstanceSnapshot? _extractedInstances;
    private InstanceSnapshot? _preparedInstances;
    private RenderWorld? _instanceRenderWorld;
    private ulong _instanceVersion;

    public static VisibilityPbrFeature CreateGpuScene(in GpuFrame frame, ReadOnlySpan<MeshPatchTree> assets,
        int instanceCapacity, VisibilityAlbedo albedo, VisibilityLodSettings lod,
        WGPUTextureFormat outputFormat, VisibilityDebugMode mode = VisibilityDebugMode.Shaded, bool enableGpuTiming = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(instanceCapacity);
        ValidateLod(lod);
        var limits = Wgpu.GetLimits(frame.Device.GetWgpu<WGPUDevice>());
        if ((ulong)System.Math.Max(1, instanceCapacity) * 192 > System.Math.Min(limits.MaxBufferSize, limits.MaxStorageBufferBindingSize)) {
            throw new ArgumentOutOfRangeException(nameof(instanceCapacity), "The instance reservation exceeds the device binding limit.");
        }
        var scene = CreateScene(assets, [], lod.Budget);
        var ranges = new AssetRoots[assets.Length];
        uint offset = 0;
        ulong maxRoots = 0, maxNodes = 0, maxFinest = 0;
        for (var i = 0; i < assets.Length; i++) {
            var tree = assets[i];
            uint meshlets = 0, triangles = 0;
            foreach (var root in tree.Nodes.Span[..tree.RootCount]) {
                meshlets = checked(meshlets + (uint)root.MeshletCount);
                triangles = checked(triangles + (uint)root.TriangleCount);
            }
            ranges[i] = new(offset, (uint)tree.RootCount, meshlets, triangles);
            offset = checked(offset + (uint)tree.Nodes.Length);
            maxRoots = System.Math.Max(maxRoots, (uint)tree.RootCount);
            maxNodes = System.Math.Max(maxNodes, (uint)(tree.Nodes.Length - tree.RootCount));
            maxFinest = System.Math.Max(maxFinest, (uint)tree.FinestTriangleCount);
        }
        var maxChildren = scene.Patches.Length == 0 ? 0u : scene.Patches.Max(p => p.Children.y);
        var roots = System.Math.Min(maxRoots * (uint)instanceCapacity, (uint)lod.Budget.MaxPatches);
        var refined = System.Math.Min(maxNodes * (uint)instanceCapacity,
            System.Math.Min((uint)lod.Budget.MaxRefinementNodes, (ulong)maxChildren * (uint)lod.Budget.MaxRefinementCandidates));
        scene = scene with {
            InstanceCapacity = instanceCapacity,
            StateCapacity = checked((uint)System.Math.Max(1ul, roots + refined)),
            TriangleCapacity = checked((uint)System.Math.Min(maxFinest * (uint)instanceCapacity, (uint)lod.Budget.MaxTriangles))
        };
        var feature = Create(in frame, scene.Geometry, [], albedo, outputFormat, mode, null, lod, enableGpuTiming, scene);
        feature._instanceWorld = frame.MainWorld;
        feature._instanceAssets = ranges;
        return feature;
    }

    public void Extract(in RenderFeatureContext<RenderFrameContext> context)
    {
        ValidateFrame(in context);
        if (_instanceWorld is null) { return; }
        if (_extractedInstances?.Frame == context.RenderWorld.FrameIndex) { return; }
        var entities = new List<Entity>();
        _instanceWorld.Query(s_InstanceMatcher, entities, static (in List<Entity> list, Entity entity) => list.Add(entity));
        if ((uint)entities.Count > InstanceCapacity) {
            throw new InvalidOperationException("The scene instance count exceeds the reserved visibility capacity.");
        }
        entities.Sort(static (a, b) => a.Id.Value.CompareTo(b.Id.Value));
        var instances = new InstanceGpu[entities.Count];
        uint roots = 0, meshlets = 0, triangles = 0;
        for (var i = 0; i < instances.Length; i++) {
            var source = entities[i].Get<VisibilityInstance>();
            if ((uint)source.AssetIndex >= (uint)_instanceAssets.Length) {
                throw new ArgumentOutOfRangeException(nameof(source.AssetIndex), "The instance asset index is outside the scene asset table.");
            }
            var asset = _instanceAssets[source.AssetIndex];
            instances[i] = ToGpu(source, new uint4(asset.Offset, asset.Count, roots, (uint)source.AssetIndex));
            roots = checked(roots + asset.Count);
            meshlets = checked(meshlets + asset.Meshlets);
            triangles = checked(triangles + asset.Triangles);
        }
        if (roots > _lod.Budget.MaxPatches || meshlets > _lod.Budget.MaxMeshlets || triangles > _lod.Budget.MaxTriangles) {
            throw new InvalidOperationException("The visibility budget cannot hold the complete scene root cut.");
        }
        _extractedInstances = new(context.RenderWorld.FrameIndex, entities.ToArray(), instances, roots);
        _instanceRenderWorld = context.RenderWorld;
    }

    private void ValidateFrame(in RenderFeatureContext<RenderFrameContext> context)
    {
        if (!ReferenceEquals(context.Frame.Frame.ResourceWorld, _world)
            || context.Frame.Frame.Device != _device || context.Frame.Frame.Queue != _queue
            || (_instanceWorld is not null && !ReferenceEquals(context.Frame.Frame.MainWorld, _instanceWorld))
            || (_instanceRenderWorld is not null && !ReferenceEquals(context.RenderWorld, _instanceRenderWorld))) {
            throw new InvalidOperationException("The visibility feature belongs to a different world/device/queue.");
        }
    }

    private void PrepareInstances(in RenderFeatureContext<RenderFrameContext> context)
    {
        if (_instanceWorld is null) { return; }
        var snapshot = _extractedInstances;
        if (snapshot is null || snapshot.Frame != context.RenderWorld.FrameIndex) {
            throw new InvalidOperationException("Extract the visibility scene before preparing its views.");
        }
        if (ReferenceEquals(snapshot, _preparedInstances)) { return; }
        var previous = _preparedInstances;
        var changed = previous is null || !snapshot.Entities.AsSpan().SequenceEqual(previous.Entities);
        var queue = _queue.GetWgpu<WGPUQueue>();
        var instances = snapshot.Instances;
        for (var start = 0; start < instances.Length;) {
            if (previous is not null && start < previous.Instances.Length && instances[start] == previous.Instances[start]) { start++; continue; }
            var end = start + 1;
            while (end < instances.Length && (previous is null || end >= previous.Instances.Length || instances[end] != previous.Instances[end])) { end++; }
            Wgpu.WriteBuffer<InstanceGpu>(queue, _geometry[4].GetWgpu<WGPUBuffer>(), (ulong)start * 192, instances.AsSpan(start, end - start));
            for (var i = start; i < end && !changed; i++) {
                changed = previous is null || i >= previous.Instances.Length
                    || !instances[i].Transform.Equals(previous.Instances[i].Transform) || !instances[i].Roots.Equals(previous.Instances[i].Roots);
            }
            start = end;
        }
        if (previous is null || previous.RootCount != snapshot.RootCount || previous.Instances.Length != instances.Length) {
            var lod = _gpuLod!.Value;
            Wgpu.WriteBuffer<uint4>(queue, lod.Parameters.GetWgpu<WGPUBuffer>(), 0,
                [new uint4((uint)(Wgpu.GetBufferSize(lod.Patches.GetWgpu<WGPUBuffer>()) / 64), snapshot.RootCount,
                    (uint)instances.Length, lod.DispatchDimension)]);
        }
        if (changed) { _instanceVersion = checked(_instanceVersion + 1); }
        InstanceCount = (uint)instances.Length;
        _preparedInstances = snapshot;
    }

    private static InstanceGpu ToGpu(VisibilityInstance instance, uint4 roots)
    {
        var transform = instance.Transform;
        var material = instance.Material;
        var determinant = math.determinant(transform);
        if (!Finite(transform) || !float.IsFinite(determinant) || determinant <= 1e-12f
            || transform.c0.w != 0 || transform.c1.w != 0 || transform.c2.w != 0 || transform.c3.w != 1) {
            throw new ArgumentException("Instances require finite, non-singular affine transforms with positive determinant.", nameof(instance));
        }
        if (!Finite(material.BaseColor) || !Finite(material.EmissiveColor)
            || !float.IsFinite(material.Metallic) || material.Metallic is < 0 or > 1
            || !float.IsFinite(material.Roughness) || material.Roughness is < 0 or > 1
            || !float.IsFinite(material.EmissiveStrength) || material.EmissiveStrength < 0) {
            throw new ArgumentException("Instance material parameters are invalid.", nameof(instance));
        }
        var normalTransform = math.transpose(math.inverse(transform));
        var emissive = material.EmissiveColor * material.EmissiveStrength;
        if (!Finite(normalTransform) || !Finite(emissive)) {
            throw new ArgumentException("Instance transforms/materials overflow their GPU representation.", nameof(instance));
        }
        return new(transform, normalTransform, new float4(material.BaseColor, 1),
            new float4(material.Metallic, MathF.Max(material.Roughness, 0.045f), 0, 0), new float4(emissive, 0), roots);
    }

    private readonly record struct AssetRoots(uint Offset, uint Count, uint Meshlets, uint Triangles);
    private sealed record InstanceSnapshot(ulong Frame, Entity[] Entities, InstanceGpu[] Instances, uint RootCount);

    private sealed partial class ViewState
    {
        public ulong InstanceVersion { get; set; }
    }
}
