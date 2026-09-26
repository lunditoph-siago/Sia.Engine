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
        var material = new PbrMaterialAsset(PbrMaterial.Default with { Roughness = 1 }, BaseColor: red, DoubleSided: true);
        var asset = PbrSceneAsset.Create([mesh], [material], [new(0, 0, float4x4.identity)]);
        foreach (var lod in new[] { false, true }) {
            using var fused = new VisibilityCacheVerification(gpu, asset, lod: lod, pbr: true, shadowsEnabled: false);
            // Roughness=1 makes the existing SSR path a no-op, retaining the independent
            // three-surface + clustered-fragment lighting implementation as a reference.
            using var reference = new VisibilityCacheVerification(gpu, asset, lod: lod, pbr: true, reference: true, shadowsEnabled: false);
            foreach (var size in new uint[] { 128, 64, 96, 128 }) {
                var expected = await reference.FrameAsync(float4x4.identity, size);
                var actual = await fused.FrameAsync(float4x4.identity, size);
                var error = expected.Zip(actual, (a, b) => System.Math.Abs(a - b)).Max();
                if (error > 2) { var worst = Enumerable.Range(0, actual.Length).MaxBy(i => System.Math.Abs(actual[i] - expected[i])); throw new InvalidOperationException($"Fused lighting differs by {error}/255: lod={lod}, size={size}, byte={worst}, actual={actual[worst]}, expected={expected[worst]}, center={string.Join(',', actual.AsSpan((64*128+64)*4,4).ToArray())}/{string.Join(',', expected.AsSpan((64*128+64)*4,4).ToArray())}."); }
                var center = (64 * 128 + 64) * 4;
                if (actual[center] <= actual[center + 1] + 20 || actual[center + 3] != 255)
                    throw new InvalidOperationException("Textured PBR fixture did not render its red material.");
            }
            using var shadowed = new VisibilityCacheVerification(gpu, asset, lod: lod, pbr: true);
            await shadowed.FrameAsync(float4x4.identity);
            var before = await shadowed.FrameAsync(float4x4.identity);
            if (shadowed._feature.FrameStatistics.ShadowsReused != 1)
                throw new InvalidOperationException("Static shadow was not reused.");
            var projection = float4x4.identity; projection.c2.x = .3f;
            await shadowed.FrameAsync(projection);
            if (shadowed._feature.FrameStatistics.ShadowsReused != 1)
                throw new InvalidOperationException("Camera movement invalidated a scene-bound shadow.");
            shadowed._sun.Get<LightColor>() = new(new(1,1,1), 0);
            var dark = await shadowed.FrameAsync(float4x4.identity);
            if (before.AsSpan().SequenceEqual(dark)) throw new InvalidOperationException("Directional lighting did not affect fused output.");
            if (lod) {
                shadowed._caster.Set(new VisibilityInstance(float4x4.Translate(new(.2f, 0, 0)), 0));
                await shadowed.FrameAsync(float4x4.identity);
                if (shadowed._feature.FrameStatistics.ShadowsRendered != 1)
                    throw new InvalidOperationException("Moved caster retained stale shadow depth.");
                shadowed._caster.Destroy();
                await shadowed.FrameAsync(float4x4.identity);
                if (shadowed._feature.FrameStatistics.ShadowsRendered != 1)
                    throw new InvalidOperationException("Removed caster retained stale shadow depth.");
            }
            var graph = fused._registry.PreparePlan().Graph;
            if (graph.Textures.Any(t => t.Descriptor.Name is "visibility-base-roughness" or "visibility-normal-metallic" or "visibility-emissive-occlusion"))
                throw new InvalidOperationException("Fused lighting retained material surfaces.");
            if (!graph.Passes.Any(p => p.Name == "pbr-frame-begin") || !graph.Passes.Any(p => p.Name == "pbr-frame-end"))
                throw new InvalidOperationException("PBR frame timing markers are absent.");
        }
        Console.WriteLine("PBR performance verification passed: fused/reference RGBA tolerance, texture/light response, live resize, camera-stable shadows and caster mutation/removal invalidation.");
    }
}
