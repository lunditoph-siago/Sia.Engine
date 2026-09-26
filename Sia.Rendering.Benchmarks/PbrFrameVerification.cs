using Sia.Engine.Lighting;
using Sia.Engine.Mesh;
using Sia.Engine.Rendering.Pbr;
using Sia.Math;

namespace Sia.Engine.Rendering.Benchmarks;

internal sealed partial class VisibilityCacheVerification
{
    public static async Task RunPbrAsync()
    {
        using var gpu = await GpuDevice.CreateAsync(true);
        var mesh = MeshPatchAsset.Cook(Assets.Grid(16));
        var red = PbrTextureData.Create(1, 1, true, [new byte[] { 230, 70, 35, 255 }]);
        var material = new PbrMaterialAsset(PbrMaterial.Default, BaseColor: red, DoubleSided: true);
        var asset = PbrSceneAsset.Create([mesh], [material], [new(0, 0, float4x4.identity)]);
        foreach (var lod in new[] { false, true }) {
            using var timed = new VisibilityCacheVerification(gpu, asset, lod: lod, pbr: true);
            using var untimed = new VisibilityCacheVerification(gpu, asset, lod: lod, pbr: true, timing: false);
            foreach (var size in new uint[] { 128, 64, 96, 128 }) {
                var expected = await untimed.FrameAsync(float4x4.identity, size);
                var actual = await timed.FrameAsync(float4x4.identity, size);
                if (!expected.AsSpan().SequenceEqual(actual))
                    throw new InvalidOperationException("GPU timing changed PBR output.");
                var center = (64 * 128 + 64) * 4;
                if (actual[center] <= actual[center + 1] + 20 || actual[center + 3] != 255)
                    throw new InvalidOperationException("Textured PBR fixture did not render its red material.");
            }
            var before = await timed.FrameAsync(float4x4.identity);
            if (!lod && timed._feature.FrameStatistics.ShadowsReused != 1)
                throw new InvalidOperationException("Static fixed-geometry shadow was not reused.");
            if (lod && timed._feature.FrameStatistics.ShadowsRendered != 1)
                throw new InvalidOperationException("Dynamic LOD shadow was not rendered.");
            timed._sun.Get<LightColor>() = new(new(1,1,1), 0);
            var dark = await timed.FrameAsync(float4x4.identity);
            if (before.AsSpan().SequenceEqual(dark))
                throw new InvalidOperationException("Directional lighting did not affect PBR output.");
            if (lod) {
                timed._caster.Set(new VisibilityInstance(float4x4.Translate(new(.2f, 0, 0)), 0));
                var moved = await timed.FrameAsync(float4x4.identity);
                if (timed._feature.FrameStatistics.ShadowsRendered != 1 || dark.AsSpan().SequenceEqual(moved))
                    throw new InvalidOperationException("Moved caster retained stale geometry/shadow depth.");
                timed._caster.Destroy();
                var removed = await timed.FrameAsync(float4x4.identity);
                if (timed._feature.FrameStatistics.ShadowsRendered != 1 || moved.AsSpan().SequenceEqual(removed))
                    throw new InvalidOperationException("Removed caster retained stale geometry/shadow depth.");
            }
        }
        Console.WriteLine("PBR frame verification passed: timed/untimed RGBA equality, texture/light response, live resizing, shadow rendering/reuse and caster mutation/removal.");
    }
}
