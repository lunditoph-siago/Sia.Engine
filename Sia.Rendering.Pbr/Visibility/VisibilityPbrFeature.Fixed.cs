using Sia.Math;
using Sia;
using Sia.WebGPU;

namespace Sia.Engine.Rendering.Pbr;

public sealed partial class VisibilityPbrFeature
{
    private readonly int? _fixedWorkCount;
    private readonly Entity _fixedWorkBuffer;

    public static VisibilityPbrFeature CreateFixedScene(in GpuFrame frame, PbrSceneAsset asset,
        ReadOnlySpan<VisibilityInstance> instances, WGPUTextureFormat outputFormat,
        VisibilityDebugMode mode = VisibilityDebugMode.Shaded)
    {
        ArgumentNullException.ThrowIfNull(asset);
        var geometry = new MeshletRasterData[asset.Geometry.Length];
        var offsets = new uint[geometry.Length];
        uint triangleOffset = 0;
        for (var i = 0; i < geometry.Length; i++) {
            var (mesh, meshlets) = asset.Geometry.Span[i].Build.Tree.CopyFinestGeometry();
            geometry[i] = MeshletRasterData.Create(mesh, meshlets);
            offsets[i] = triangleOffset;
            triangleOffset = checked(triangleOffset + (uint)geometry[i].Triangles.Length);
        }
        var ranges = new uint4[instances.Length];
        var capacity = 0;
        for (var i = 0; i < instances.Length; i++) {
            var index = instances[i].AssetIndex;
            if ((uint)index >= (uint)geometry.Length) { throw new ArgumentOutOfRangeException(nameof(instances)); }
            ranges[i] = new(0, 0, 0, (uint)index);
            capacity = checked(capacity + geometry[index].Triangles.Length);
        }
        var work = new WorkGpu[capacity];
        var count = 0;
        for (var instance = 0; instance < instances.Length; instance++) {
            var index = instances[instance].AssetIndex;
            for (uint triangle = 0; triangle < geometry[index].Triangles.Length; triangle++) {
                work[count++] = new(offsets[index] + triangle, (uint)instance);
            }
        }
        var scene = new SceneLodData(MeshletRasterData.Combine(geometry), [], ranges, default, 1, (uint)capacity);
        return Create(in frame, scene.Geometry, instances, null, outputFormat, mode, null, default,
            scene: scene, materials: asset.Materials.Span, fixedWork: work);
    }
}
