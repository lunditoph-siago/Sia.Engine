using Sia.Engine.Mesh;
using Sia.Engine.Rendering;
using Sia.Engine.Rendering.Pbr;
using Sia.Math;
using Xunit;

namespace Sia.Rendering.Debug.Tests;

public sealed class TransportCompositionTests
{
    private static PbrSceneAsset Source()
    {
        static MeshPatchAsset Mesh(float x) => MeshPatchAsset.Cook(new([
            new(new(x, 0, 0), new(0, 0, 1), new(0)),
            new(new(x + 1, 0, 0), new(0, 0, 1), new(0)),
            new(new(x, 1, 0), new(0, 0, 1), new(0))], [0, 1, 2], new(new(x, 0, 0), new(x + 1, 1, 0))));
        var color = PbrTextureData.Create(1, 1, true, [new byte[] { 128, 64, 255, 255 }]);
        var metal = PbrTextureData.Create(1, 1, false, [new byte[] { 0, 0, 128, 255 }]);
        var emissive = PbrTextureData.Create(1, 1, true, [new byte[] { 64, 255, 128, 255 }]);
        var material = new PbrMaterialAsset(new() {
            BaseColor = new(.5f, .25f, .75f), Metallic = .5f,
            EmissiveColor = new(1, 2, 3), EmissiveStrength = 4
        }, BaseColor: color, MetallicRoughness: metal, Emissive: emissive, DoubleSided: true);
        var moved = new float4x4(new(0, 2, 0, 0), new(-3, 0, 0, 0), new(0, 0, .5f, 0), new(4, 5, 6, 1));
        // Valid positive determinant, but world-space area is below the trace threshold.
        var tiny = new float4x4(new(1e-5f, 0, 0, 0), new(0, 1e-5f, 0, 0), new(0, 0, 1e5f, 0), new(0, 0, 0, 1));
        return PbrSceneAsset.Create([Mesh(0), Mesh(3)], [material, material with { AlphaBlend = true }], [
            new(0, 0, float4x4.identity), new(1, 0, float4x4.identity),
            new(0, 0, moved) { Dynamic = true }, new(0, 1, float4x4.identity), new(0, 0, tiny)
        ]);
    }

    private static SceneTraceTriangle Triangle(int slot)
    {
        static float Linear(byte b) => MathF.Pow((b / 255f + .055f) / 1.055f, 2.4f);
        var albedo = new float3(.5f, .25f, .75f) * new float3(Linear(128), Linear(64), 1)
            * (1 - .5f * (128 / 255f));
        var emission = new float3(1, 2, 3) * 4 * new float3(Linear(64), 1, Linear(128));
        return slot switch {
            0 => new(new(0, 0, 0), new(1, 0, 0), new(0, 1, 0), albedo, emission, true),
            1 => new(new(3, 0, 0), new(4, 0, 0), new(3, 1, 0), albedo, emission, true),
            _ => new(new(4, 5, 6), new(4, 7, 6), new(1, 5, 6), albedo, emission, true)
        };
    }

    private static void EqualTrace(SceneTraceData actual, int[] slots, byte[] identity)
    {
        var expected = new SceneTraceData(slots.Select(Triangle).ToArray(), sceneIdentity: identity);
        Assert.Equal(slots.Length, actual.TriangleCount);
        Assert.Equal(expected.Bounds, actual.Bounds);
        Assert.Equal(expected.Identity.ToArray(), actual.Identity.ToArray());
        Assert.Equal(expected.Packed.ToArray(), actual.Packed.ToArray());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ResidentRootAndFinestKeepAnalyticGeometryMaterialsAndDomains(bool staticOnly, bool finest)
    {
        var source = Source();
        var actual = staticOnly ? PbrSceneTransport.BuildStatic(source, finest: finest) : PbrSceneTransport.Build(source, finest: finest);
        var identity = staticOnly ? PbrSceneTransport.StaticIdentity(source, finest) : PbrSceneTransport.Identity(source, finest);
        EqualTrace(actual, staticOnly ? [0, 1] : [0, 1, 2], identity);
        Assert.Throws<InvalidOperationException>(() => PbrSceneTransport.Build(source, 0, finest));
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    public async Task MixedStreamKeepsVirtualThenConventionalOrderAndStaticDomain(bool staticOnly, int conventional)
    {
        var source = Source();
        var chunks = new Dictionary<string, byte[]>();
        var metadata = await PbrSceneStream.CookAsync(source, (chunk, bytes, _) => {
            chunks[chunk.Id] = bytes.ToArray();
            return ValueTask.CompletedTask;
        }, new int[] { conventional });
        await using var stream = await PbrSceneStream.OpenAsync(metadata, (chunk, _) =>
            ValueTask.FromResult<Stream>(new MemoryStream(chunks[chunk.Id], false)));
        var actual = staticOnly ? PbrSceneTransport.BuildStatic(stream) : PbrSceneTransport.Build(stream);
        var order = conventional == 0 ? new[] { 1, 0 } : new[] { 0, 1 };
        EqualTrace(actual, staticOnly ? order : [.. order, 2],
            staticOnly ? stream.StaticIdentity.ToArray() : stream.Identity.ToArray());
        Assert.Throws<InvalidOperationException>(() => PbrSceneTransport.Build(stream, 0));
    }

    [Fact]
    public void DynamicTransportKeepsAnalyticLiveGeometryAndDerivedIdentity()
    {
        var source = Source();
        var actual = PbrSceneTransport.BuildDynamic(source, [source.Instances.Span[2]], PbrSceneTransport.DynamicMaximumBytes(source))!;
        EqualTrace(actual, [2], []);
    }
}
