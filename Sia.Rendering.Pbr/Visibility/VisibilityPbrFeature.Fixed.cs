using Sia.Math;
using Sia;
using Sia.Engine.Mesh;
using Sia.WebGPU;

namespace Sia.Engine.Rendering.Pbr;

public sealed partial class VisibilityPbrFeature
{
    private readonly FixedGeometryGpu? _fixedGeometry;

    private uint4 RasterConfig => _fixedGeometry is { } geometry
        ? new(geometry.Stride, geometry.Count, geometry.DispatchDimension, geometry.SharedVertices ? 1u : 3u) : new(1, 0, _materialTiles.DispatchDimension, 0);

    public static VisibilityPbrFeature CreateFixedScene(in GpuFrame frame, PbrSceneAsset asset,
        ReadOnlySpan<VisibilityInstance> instances, WGPUTextureFormat outputFormat,
        VisibilityDebugMode mode = VisibilityDebugMode.Shaded)
    {
        ArgumentNullException.ThrowIfNull(asset);
        var geometry = new MeshletRasterData[asset.Geometry.Length];
        var clusters = new Meshlet[geometry.Length][];
        var offsets = new uint[geometry.Length];
        uint triangleOffset = 0;
        for (var i = 0; i < geometry.Length; i++) {
            var (mesh, meshlets) = asset.Geometry.Span[i].Build.Tree.CopyFinestGeometry();
            geometry[i] = MeshletRasterData.Create(mesh, meshlets);
            clusters[i] = meshlets.Meshlets;
            offsets[i] = triangleOffset;
            triangleOffset = checked(triangleOffset + (uint)geometry[i].Triangles.Length);
        }
        var combined = MeshletRasterData.Combine(geometry);
        var packedOffset = (uint)(combined.Indices.Length - combined.Triangles.Length);
        var ranges = new uint4[instances.Length];
        var capacity = 0;
        var clusterCount = 0;
        for (var i = 0; i < instances.Length; i++) {
            var index = instances[i].AssetIndex;
            if ((uint)index >= (uint)geometry.Length) { throw new ArgumentOutOfRangeException(nameof(instances)); }
            ranges[i] = new(0, 0, 0, (uint)index);
            capacity = checked(capacity + geometry[index].Triangles.Length);
            clusterCount = checked(clusterCount + clusters[index].Length);
        }
        var work = new FixedClusterGpu[clusterCount];
        var count = 0;
        for (var instance = 0; instance < instances.Length; instance++) {
            var index = instances[instance].AssetIndex;
            foreach (var cluster in clusters[index]) {
                work[count++] = new(new(cluster.Bounds.Box.Min, 0), new(cluster.Bounds.Box.Max, 0),
                    new(cluster.Bounds.Center, cluster.Bounds.Radius), new(cluster.Bounds.ConeAxis, cluster.Bounds.ConeCutoff),
                    new(offsets[index] + (uint)cluster.TriangleOffset / 3, (uint)instance, (uint)cluster.TriangleCount,
                        packedOffset + offsets[index] + (uint)cluster.TriangleOffset / 3));
            }
        }
        var scene = new SceneLodData(combined, [], ranges, default, 1, (uint)capacity,
            asset.Geometry.ToArray().Select(mesh => PatchBounds(mesh.Build.Tree)).ToArray());
        return Create(in frame, scene.Geometry, instances, null, outputFormat, mode, null, default,
            scene: scene, materials: asset.Materials.Span, fixedClusters: work);
    }
}
