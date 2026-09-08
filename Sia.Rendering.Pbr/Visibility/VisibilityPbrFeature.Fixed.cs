using Sia.Math;
using Sia.WebGPU;

namespace Sia.Engine.Rendering.Pbr;

public sealed partial class VisibilityPbrFeature
{
    private readonly uint4[]? _fixedWork;

    public static VisibilityPbrFeature CreateFixedScene(in GpuFrame frame, PbrSceneAsset asset,
        ReadOnlySpan<VisibilityInstance> instances, WGPUTextureFormat outputFormat,
        VisibilityDebugMode mode = VisibilityDebugMode.Shaded)
    {
        ArgumentNullException.ThrowIfNull(asset);
        var trees = asset.Geometry.Span.ToArray().Select(mesh => mesh.Build.Tree).ToArray();
        var scene = CreateScene(trees, instances, new(int.MaxValue, int.MaxValue, int.MaxValue));
        var work = new uint4[checked((int)scene.TriangleCapacity)];
        var count = 0;
        for (var instance = 0; instance < instances.Length; instance++) {
            var range = scene.InstanceRoots[instance];
            var length = trees[instances[instance].AssetIndex].Nodes.Length;
            foreach (var patch in scene.Patches.AsSpan((int)range.x, length)) {
                if (patch.Children.y != 0) { continue; }
                for (uint triangle = 0; triangle < patch.Geometry.z; triangle++) {
                    work[count++] = new(patch.Geometry.y + triangle, (uint)instance, 0, 0);
                }
            }
        }
        return Create(in frame, scene.Geometry, instances, null, outputFormat, mode, null, default,
            scene: scene, materials: asset.Materials.Span, fixedWork: work);
    }
}
