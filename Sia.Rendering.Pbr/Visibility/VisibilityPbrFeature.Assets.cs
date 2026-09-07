using Sia.Engine.Mesh;
using Sia.Math;
using Sia.WebGPU;

namespace Sia.Engine.Rendering.Pbr;

public sealed partial class VisibilityPbrFeature
{
    public static VisibilityPbrFeature CreateGpuLod(in GpuFrame frame, ReadOnlySpan<MeshPatchTree> assets,
        ReadOnlySpan<VisibilityInstance> instances, VisibilityAlbedo albedo, VisibilityLodSettings lod,
        WGPUTextureFormat outputFormat, VisibilityDebugMode mode = VisibilityDebugMode.Shaded, bool enableGpuTiming = false)
    {
        ValidateLod(lod);
        var scene = CreateScene(assets, instances, lod.Budget);
        if (scene.RootCost.x > (uint)lod.Budget.MaxPatches || scene.RootCost.y > (uint)lod.Budget.MaxMeshlets
            || scene.RootCost.z > (uint)lod.Budget.MaxTriangles) {
            throw new ArgumentException("The visibility budget cannot hold the complete scene root cut.", nameof(lod));
        }
        return Create(in frame, scene.Geometry, instances, albedo, outputFormat, mode, null, lod, enableGpuTiming, scene);
    }

    private static SceneLodData CreateScene(ReadOnlySpan<MeshPatchTree> assets,
        ReadOnlySpan<VisibilityInstance> instances, MeshPatchBudget budget)
    {
        foreach (var asset in assets) { ArgumentNullException.ThrowIfNull(asset); }
        ulong roots = 0, rootMeshlets = 0, rootTriangles = 0, finest = 0, instanceNodes = 0;
        foreach (var instance in instances) {
            if ((uint)instance.AssetIndex >= (uint)assets.Length) {
                throw new ArgumentOutOfRangeException(nameof(instances), "The instance asset index is outside the scene asset table.");
            }
            var tree = assets[instance.AssetIndex];
            roots += (uint)tree.RootCount;
            instanceNodes += (uint)tree.Nodes.Length;
            finest += (uint)tree.FinestTriangleCount;
            foreach (var root in tree.Nodes.Span[..tree.RootCount]) {
                rootMeshlets += (uint)root.MeshletCount;
                rootTriangles += (uint)root.TriangleCount;
            }
        }
        var geometry = new MeshletRasterData[assets.Length];
        var offsets = new uint[assets.Length];
        var patches = new List<PatchGpu>();
        uint triangleOffset = 0, maxChildren = 0;
        for (var asset = 0; asset < assets.Length; asset++) {
            var tree = assets[asset];
            var (mesh, meshlets) = tree.CopyGeometry();
            geometry[asset] = MeshletRasterData.Create(mesh, meshlets);
            offsets[asset] = (uint)patches.Count;
            var nodes = tree.Nodes.Span;
            foreach (var node in nodes) {
                maxChildren = System.Math.Max(maxChildren, (uint)node.ChildCount);
                uint childMeshlets = 0, childTriangles = 0;
                for (var child = node.ChildOffset; child < node.ChildOffset + node.ChildCount; child++) {
                    childMeshlets = checked(childMeshlets + (uint)nodes[child].MeshletCount);
                    childTriangles = checked(childTriangles + (uint)nodes[child].TriangleCount);
                }
                patches.Add(new(new float4(node.Bounds.Min, node.EstimatedSpatialError), new float4(node.Bounds.Max, 0),
                    new uint4(checked(offsets[asset] + (uint)node.ChildOffset), (uint)node.ChildCount, childMeshlets, childTriangles),
                    new uint4((uint)node.MeshletCount, checked(triangleOffset + (uint)node.TriangleOffset), (uint)node.TriangleCount, (uint)asset)));
            }
            triangleOffset = checked(triangleOffset + (uint)geometry[asset].Triangles.Length);
        }
        var ranges = new uint4[instances.Length];
        uint rootOffset = 0;
        for (var i = 0; i < instances.Length; i++) {
            var asset = instances[i].AssetIndex;
            var count = (uint)assets[asset].RootCount;
            ranges[i] = new(offsets[asset], count, rootOffset, (uint)asset);
            rootOffset = checked(rootOffset + count);
        }
        var refined = System.Math.Min(instanceNodes - roots,
            System.Math.Min((uint)budget.MaxRefinementNodes, (ulong)maxChildren * (uint)budget.MaxRefinementCandidates));
        return new(geometry.Length == 1 ? geometry[0] : MeshletRasterData.Combine(geometry), patches.ToArray(), ranges,
            new uint3(rootOffset, checked((uint)rootMeshlets), checked((uint)rootTriangles)), checked((uint)System.Math.Max(1ul, roots + refined)),
            checked((uint)System.Math.Min(finest, System.Math.Max(rootTriangles, (uint)budget.MaxTriangles))));
    }

    private sealed record SceneLodData(MeshletRasterData Geometry, PatchGpu[] Patches, uint4[] InstanceRoots,
        uint3 RootCost, uint StateCapacity, uint TriangleCapacity);
}
