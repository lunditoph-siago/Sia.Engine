using System.Buffers.Binary;
using System.Security.Cryptography;
using Sia.Engine.Mesh;
using Sia.Engine.Rendering;
using Sia.Engine.Rendering.Pbr;
using Sia.Math;
using Xunit;

namespace Sia.Rendering.Debug.Tests;

public sealed class LightmapBakeTests
{
    private static MeshPatchAsset Quad() => MeshPatchAsset.Cook(new([
        new(new(-1, -1, 0), new(0, 0, 1), new(0, 0)), new(new(1, -1, 0), new(0, 0, 1), new(1, 0)),
        new(new(1, 1, 0), new(0, 0, 1), new(1, 1)), new(new(-1, 1, 0), new(0, 0, 1), new(0, 1))],
        [0, 1, 2, 0, 2, 3], new(new(-1, -1, 0), new(1, 1, 0))));

    private static ProceduralSky Constant(float value) => new() {
        Horizon = new(value), Zenith = new(value), Ground = new(value), SunRadiance = default
    };

    private static PbrSceneAsset Scene(bool ceiling = false, bool dynamic = false, float receiverColor = 1)
    {
        var material = new PbrMaterialAsset(new() { BaseColor = new(receiverColor), Roughness = .8f }, DoubleSided: true);
        var slots = new List<PbrSceneInstance> { new(0, 0, float4x4.identity) };
        if (ceiling) slots.Add(new(0, 0, new(new(20, 0, 0, 0), new(0, 20, 0, 0), new(0, 0, 1, 0), new(0, 0, .2f, 1))) { Dynamic = dynamic });
        return PbrSceneAsset.Create([Quad()], [material], slots.ToArray());
    }

    [Fact]
    public void UnoccludedConstantSkyHasAnalyticIrradianceAndNoReceiverAlbedoMultiplication()
    {
        var settings = new PbrLightmapBakeSettings { Sky = Constant(2), Samples = 256 };
        var input = PbrLightmapInput.Create(Scene(receiverColor: .1f), 16);
        var asset = PbrLightmapBaker.Bake(input, settings);
        foreach (var n in new[] { new float3(0, 0, 1), math.normalize(new float3(1, 2, 3)), new float3(0, 1, 0) }) {
            var e = asset.Irradiance(8, 8, n);
            Assert.InRange(e.x, 2 * MathF.PI - .025f, 2 * MathF.PI + .025f);
            Assert.Equal(e.x, e.y);
            Assert.Equal(e.x, e.z);
        }
        var other = PbrLightmapBaker.Bake(PbrLightmapInput.Create(Scene(), 16), settings);
        Assert.Equal(asset.Irradiance(8, 8, new(0, 0, 1)), other.Irradiance(8, 8, new(0, 0, 1)));
        Assert.Equal(asset.Irradiance(8, 8, new(0, 0, 1)), asset.Irradiance(0, 0, new(0, 0, 1))); // Dilated border.
        Assert.True(asset.Data.Span.SequenceEqual(PbrLightmapBaker.Bake(input, settings).Data.Span));
        Assert.True(asset.BakeIdentity.Span.SequenceEqual(PbrLightmapBaker.Bake(input, settings).BakeIdentity.Span));
    }

    [Fact]
    public void StaticOcclusionDarkensReceiverAndDynamicOccluderIsExcludedFromBake()
    {
        var settings = new PbrLightmapBakeSettings { Sky = Constant(1), Samples = 256 };
        var open = PbrLightmapBaker.Bake(PbrLightmapInput.Create(Scene(), 16), settings);
        var closed = PbrLightmapBaker.Bake(PbrLightmapInput.Create(Scene(true), 16), settings);
        var moving = PbrLightmapBaker.Bake(PbrLightmapInput.Create(Scene(true, true), 16), settings);
        var normal = new float3(0, 0, 1);
        Assert.InRange(open.Irradiance(8, 8, normal).x, 3.12f, 3.17f);
        Assert.True(closed.Irradiance(8, 8, normal).x < open.Irradiance(8, 8, normal).x * .15f);
        Assert.Equal(open.Irradiance(8, 8, normal), moving.Irradiance(8, 8, normal));
        Assert.True(open.SceneIdentity.Span.SequenceEqual(moving.SceneIdentity.Span));
        Assert.True(open.BakeIdentity.Span.SequenceEqual(moving.BakeIdentity.Span));
        Assert.False(open.SceneIdentity.Span.SequenceEqual(closed.SceneIdentity.Span));
    }

    [Fact]
    public void DirectionalDirectIsExcludedWhileVisibleBounceAndEmissionReachReceiver()
    {
        var settings = new PbrLightmapBakeSettings {
            Sky = Constant(0), Samples = 512,
            TowardLight = math.normalize(new float3(-1, 0, 1)), LightRadiance = new(3)
        };
        var open = PbrLightmapBaker.Bake(PbrLightmapInput.Create(Scene(), 16), settings);
        Assert.Equal(float3.zero, open.Irradiance(8, 8, new(0, 0, 1)));
        PbrSceneAsset Corner(float3 albedo, float3 emission) => PbrSceneAsset.Create([Quad()], [
            new PbrMaterialAsset(new() { BaseColor = new(1), Roughness = .8f }, DoubleSided: true),
            new PbrMaterialAsset(new() { BaseColor = albedo, EmissiveColor = emission, EmissiveStrength = 1, Roughness = .8f }, DoubleSided: true)], [
            new(0, 0, float4x4.identity),
            new(0, 1, new(new(0, 0, 1, 0), new(0, 1, 0, 0), new(-1, 0, 0, 0), new(1, 0, 1, 1)))]);
        var bounce = PbrLightmapBaker.Bake(PbrLightmapInput.Create(Corner(new(1), default), 16), settings);
        var e = bounce.Irradiance(8, 8, new(0, 0, 1));
        Assert.InRange(e.x, .05f, 3);
        Assert.Equal(e.x, e.y);
        Assert.Equal(e.x, e.z);
        var emissive = PbrLightmapBaker.Bake(PbrLightmapInput.Create(Corner(default, new(2, 0, 0)), 16),
            settings with { LightRadiance = default });
        var red = emissive.Irradiance(8, 8, new(0, 0, 1));
        Assert.InRange(red.x, .1f, 6.3f);
        Assert.Equal(0, red.y);
        Assert.Equal(0, red.z);
    }

    [Fact]
    public void LightmapCodecAndManifestRejectStaleStateNonfiniteValuesAndBudgetViolations()
    {
        var input = PbrLightmapInput.Create(Scene(), 16);
        var settings = new PbrLightmapBakeSettings { Sky = Constant(1), Samples = 64 };
        var baked = PbrLightmapBaker.Bake(input, settings);
        var encoded = baked.Encode();
        var restored = PbrLightmapAsset.Decode(encoded);
        Assert.Equal(encoded, restored.Encode());
        Assert.True(baked.Data.Span.SequenceEqual(restored.Data.Span));
        Assert.True(baked.BakeIdentity.Span.SequenceEqual(restored.BakeIdentity.Span));
        Assert.False(baked.BakeIdentity.Span.SequenceEqual(PbrLightmapBaker.Bake(input, settings with { Samples = 128 }).BakeIdentity.Span));
        Assert.False(baked.BakeIdentity.Span.SequenceEqual(PbrLightmapBaker.Bake(input, settings with { Sky = Constant(2) }).BakeIdentity.Span));
        Assert.False(baked.BakeIdentity.Span.SequenceEqual(PbrLightmapBaker.Bake(input, settings with { LightRadiance = new(1) }).BakeIdentity.Span));
        Assert.Throws<InvalidDataException>(() => PbrLightmapAsset.Decode(encoded, encoded.Length - 1));
        Assert.Throws<InvalidDataException>(() => PbrLightmapAsset.Decode(encoded.AsSpan(0, encoded.Length - 1)));
        var corrupt = encoded.ToArray();
        corrupt[0] = (byte)'X';
        Assert.Throws<InvalidDataException>(() => PbrLightmapAsset.Decode(corrupt));
        corrupt = encoded.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(corrupt.AsSpan(224 + 16 * baked.Receivers.Length + 20 * baked.Charts.Length),
            BitConverter.HalfToUInt16Bits(Half.NaN));
        SHA256.HashData(corrupt.AsSpan(0, corrupt.Length - 32)).CopyTo(corrupt.AsSpan(corrupt.Length - 32));
        Assert.Throws<InvalidDataException>(() => PbrLightmapAsset.Decode(corrupt));
        Assert.Throws<ArgumentException>(() => PbrLightmapBaker.Bake(input, settings with { Sky = Constant(float.NaN) }));
        Assert.Throws<InvalidOperationException>(() => PbrLightmapBaker.Bake(input, settings, maximumCoefficientBytes: 16));
        Assert.Throws<OperationCanceledException>(() => PbrLightmapBaker.Bake(input, settings, cancellationToken: new(true)));
    }
}
