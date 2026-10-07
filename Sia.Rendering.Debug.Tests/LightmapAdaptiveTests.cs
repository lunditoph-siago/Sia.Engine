using Sia.Engine.Mesh;
using Sia.Engine.Rendering.Pbr;
using Sia.Math;
using Xunit;

namespace Sia.Rendering.Debug.Tests;

public sealed class LightmapAdaptiveTests
{
    private static MeshPatchAsset Quad(bool twoIslands = false)
    {
        MeshVertex[] vertices = [new(new(0, 0, 0), new(0, 0, 1), new(0, 0)),
            new(new(1, 0, 0), new(0, 0, 1), new(1, 0)), new(new(1, 1, 0), new(0, 0, 1), new(1, 1)),
            new(new(0, 1, 0), new(0, 0, 1), new(0, 1))];
        uint[] indices = [0, 1, 2, 0, 2, 3];
        if (twoIslands) {
            vertices = vertices.Concat(vertices.Select(v => v with { Position = v.Position + new float3(2, 0, 0) })).ToArray();
            indices = indices.Concat(indices.Select(i => i + 4)).ToArray();
        }
        return MeshPatchAsset.Cook(new(vertices, indices, new(float3.zero, new(twoIslands ? 3 : 1, 1, 0))));
    }

    private static float4x4 Translate(float x) => new(new(1, 0, 0, 0), new(0, 1, 0, 0), new(0, 0, 1, 0), new(x, 0, 0, 1));
    private static PbrLightmapBakeSettings Sky() => new() {
        Sky = new() { Horizon = new(1), Zenith = new(1), Ground = new(1), SunRadiance = default }, Samples = 128
    };
    private static PbrMaterialAsset Material() => new(new() { BaseColor = new(1), Roughness = .8f }, DoubleSided: true);

    [Fact]
    public void ReceiverAtATimeMatchesDenseReferenceWithLessWorkingMemory()
    {
        var scene = PbrSceneAsset.Create([Quad()], [Material()], [new(0, 0, Translate(0)), new(0, 0, Translate(4))]);
        var original = scene.Encode();
        var input = PbrLightmapInput.Create(scene, 16);
        var reference = PbrLightmapBaker.Bake(input, Sky());
        const ulong tileWorking = 16 * 16 * (PbrLightmapTexel.Stride + 4 + 64);
        Assert.Throws<InvalidOperationException>(() => PbrLightmapBaker.Bake(input, Sky(), maximumCoefficientBytes: tileWorking));
        var asset = PbrLightmapBaker.BakeAdaptive(scene, out var mapped, Sky(), 16, 16,
            maximumAssetBytes: (ulong)reference.Encode().Length, maximumWorkingBytes: tileWorking);
        Assert.Equal(reference.Encode(), asset.Encode());
        Assert.Equal(input.Scene.Encode(), mapped.Encode());
        Assert.Equal(original, scene.Encode());
        Assert.Equal(asset.Encode(), PbrLightmapAsset.Decode(asset.Encode()).Encode());
        Assert.Throws<InvalidOperationException>(() => PbrLightmapBaker.BakeAdaptive(scene, out _, Sky(), 16, 16,
            maximumWorkingBytes: tileWorking - 1));
        Assert.Throws<InvalidOperationException>(() => PbrLightmapBaker.BakeAdaptive(scene, out _, Sky(), 16, 16,
            maximumAssetBytes: (ulong)reference.Encode().Length - 1));
    }

    [Fact]
    public void DifferentChartCapacityGetsDifferentTilesAndDynamicChangesKeepBakeValid()
    {
        var geometry = new[] { Quad(), Quad(true) };
        var scene = PbrSceneAsset.Create(geometry, [Material()], [new(0, 0, Translate(0)), new(1, 0, Translate(4)),
            new(1, 0, Translate(20)) { Dynamic = true }]);
        var asset = PbrLightmapBaker.BakeAdaptive(scene, out var mapped, Sky(), 8, 16);
        Assert.Equal(32, asset.Resolution);
        Assert.Equal(new[] { 8, 16 }, asset.Receivers.ToArray().Select(r => r.Resolution));
        Assert.Equal(new[] { 0, 1 }, asset.Receivers.ToArray().Select(r => r.StaticInstance));
        Assert.Equal((16, 0), (asset.Receivers.Span[0].X, asset.Receivers.Span[0].Y));
        Assert.Equal((0, 0), (asset.Receivers.Span[1].X, asset.Receivers.Span[1].Y));
        foreach (var r in asset.Receivers.Span) {
            var covered = 0;
            for (var y = 0; y < r.Resolution; y++)
                for (var x = 0; x < r.Resolution; x++) {
                    var value = asset.Irradiance(r.X + x, r.Y + y, new(0, 0, 1));
                    if (value.x == 0) continue;
                    covered++;
                    Assert.InRange(value.x, MathF.PI - .025f, MathF.PI + .025f);
                }
            Assert.True(covered > 0);
        }
        Assert.Equal(float3.zero, asset.Irradiance(31, 31, new(0, 0, 1)));
        Assert.True(PbrLightmapAsset.Identity(mapped).AsSpan().SequenceEqual(asset.SceneIdentity.Span));
        var shifted = PbrSceneAsset.Create([geometry[1], geometry[0]], [Material()], [
            new(0, 0, Translate(-50)) { Dynamic = true }, new(1, 0, Translate(0)), new(0, 0, Translate(4))]);
        var again = PbrLightmapBaker.BakeAdaptive(shifted, out _, Sky(), 8, 16);
        Assert.Equal(asset.Encode(), again.Encode());
        Assert.True(mapped.Instances.Span[2].Dynamic);
    }

    [Fact]
    public void AdaptiveLimitsRejectBadBoundsCapacityAndCancellation()
    {
        var scene = PbrSceneAsset.Create([Quad(true)], [Material()], [new(0, 0, Translate(0))]);
        Assert.Throws<ArgumentException>(() => PbrLightmapBaker.BakeAdaptive(scene, out _, Sky(), 12, 16));
        Assert.Throws<ArgumentException>(() => PbrLightmapBaker.BakeAdaptive(scene, out _, Sky(), 16, 8));
        Assert.Throws<InvalidOperationException>(() => PbrLightmapBaker.BakeAdaptive(scene, out _, Sky(), 8, 8));
        Assert.Throws<InvalidOperationException>(() => PbrLightmapBaker.BakeAdaptive(scene, out _, Sky(), 8, 16, maximumAtlasResolution: 8));
        Assert.Throws<OperationCanceledException>(() => PbrLightmapBaker.BakeAdaptive(scene, out _, Sky(), cancellationToken: new(true)));
        Assert.False(scene.Geometry.Span[0].HasLightmapUV);
    }
}
