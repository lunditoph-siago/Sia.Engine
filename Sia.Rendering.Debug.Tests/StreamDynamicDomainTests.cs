using System.Text.Json;
using System.Text.Json.Nodes;
using Sia.Engine.Mesh;
using Sia.Engine.Rendering.Pbr;
using Sia.Math;
using Xunit;

namespace Sia.Rendering.Debug.Tests;

public sealed class StreamDynamicDomainTests
{
    private static PbrSceneAsset Scene()
    {
        var mesh = MeshPatchAsset.Cook(new([
            new(new(0, 0, 0), new(0, 0, 1), new(0)) { LightmapUV = new(.1f) },
            new(new(1, 0, 0), new(0, 0, 1), new(1, 0)) { LightmapUV = new(.9f, .1f) },
            new(new(0, 1, 0), new(0, 0, 1), new(0, 1)) { LightmapUV = new(.1f, .9f) }],
            [0, 1, 2], new(new(0), new(1))));
        return PbrSceneAsset.Create([mesh], [new(new()), new(new(), AlphaBlend: true)], [
            new(0, 0, float4x4.identity) { Dynamic = true },
            new(0, 0, float4x4.identity),
            new(0, 1, float4x4.identity),
            new(0, 1, float4x4.identity) { Dynamic = true },
            new(0, 0, float4x4.identity)]);
    }

    [Fact]
    public async Task VersionThreeStaticStreamsRetainTheirOriginalOrdinalContract()
    {
        var original = Scene();
        var source = PbrSceneAsset.Create(original.Geometry.Span, original.Materials.Span,
            original.Instances.ToArray().Where(i => !i.Dynamic).ToArray());
        var chunks = new Dictionary<string, byte[]>();
        var metadata = await PbrSceneStream.CookAsync(source, (c, b, _) => {
            chunks[c.Id] = b.ToArray(); return ValueTask.CompletedTask;
        });
        var header = JsonNode.Parse(metadata)!.AsObject();
        header["Version"] = 3;
        header.Remove("StaticSourceInstances");
        await using var stream = await PbrSceneStream.OpenAsync(JsonSerializer.SerializeToUtf8Bytes(header), (c, _) =>
            ValueTask.FromResult<Stream>(new MemoryStream(chunks[c.Id], false)));
        Assert.Equal(new[] { 0, 1, 2 }, stream.StaticSourceInstances.ToArray());
        Assert.Equal(PbrLightmapAsset.Identity(source), stream.LightmapIdentity.ToArray());
        PbrGpuScene.ValidateLightmapSource(stream.Bootstrap, stream, stream.LightmapIdentity.Span,
            [new(-1, 0, 0, 0, 8, default), new(-1, 2, 8, 0, 8, default)]);
    }

    [Fact]
    public async Task SharedAssetSplitsMobilityAndPreservesCanonicalStaticLightmapDomain()
    {
        var source = Scene();
        var chunks = new Dictionary<string, byte[]>();
        var metadata = await PbrSceneStream.CookAsync(source, (c, b, _) => {
            chunks[c.Id] = b.ToArray(); return ValueTask.CompletedTask;
        });
        await using var stream = await PbrSceneStream.OpenAsync(metadata, (c, _) =>
            ValueTask.FromResult<Stream>(new MemoryStream(chunks[c.Id], false)));
        Assert.Equal(5, stream.SourceInstanceCount);
        Assert.Equal(new[] { 1, 4 }, stream.OpaqueSourceInstances.ToArray());
        Assert.Equal(new[] { 0, 2, 3 }, stream.BootstrapSourceInstances.ToArray());
        Assert.Equal(new[] { -1, 0, 1, -1, 2 }, stream.StaticSourceInstances.ToArray());
        Assert.Equal(new[] { true, false, true }, stream.Bootstrap.Instances.ToArray().Select(i => i.Dynamic));
        Assert.Equal(PbrSceneTransport.StaticIdentity(source), stream.StaticIdentity.ToArray());
        var trace = PbrSceneTransport.BuildStatic(stream);
        Assert.Equal(2, trace.TriangleCount);
        Assert.Equal(3, PbrSceneTransport.Build(stream).TriangleCount);
        Assert.Equal(PbrSceneTransport.BuildStatic(source).Packed.ToArray(), trace.Packed.ToArray());
        Assert.Equal(PbrSceneTransport.DynamicMaximumBytes(source), PbrSceneTransport.DynamicMaximumBytes(stream.Bootstrap));
        PbrLightmapReceiver[] receivers = [new(-1, 0, 0, 0, 8, default), new(-1, 2, 8, 0, 8, default)];
        PbrGpuScene.ValidateLightmapSource(stream.Bootstrap, stream, stream.LightmapIdentity.Span, receivers);
        Assert.Throws<ArgumentException>(() => PbrGpuScene.ValidateLightmapSource(stream.Bootstrap, stream,
            stream.LightmapIdentity.Span, [receivers[0], new(-1, 4, 8, 0, 8, default)]));
        Assert.Throws<ArgumentException>(() => PbrGpuScene.ValidateLightmapSource(stream.Bootstrap, stream,
            stream.LightmapIdentity.Span, [.. receivers, new(-1, 1, 0, 8, 8, default)]));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("short")]
    [InlineData("negative")]
    [InlineData("gap")]
    [InlineData("virtual-dynamic")]
    public async Task InvalidStaticOrdinalsFailBeforeChunkIo(string mutation)
    {
        var metadata = await PbrSceneStream.CookAsync(Scene(), (_, _, _) => ValueTask.CompletedTask);
        var header = JsonNode.Parse(metadata)!.AsObject();
        switch (mutation) {
            case "missing": header.Remove("StaticSourceInstances"); break;
            case "short": header["StaticSourceInstances"] = JsonSerializer.SerializeToNode(new[] { -1 }); break;
            case "negative": header["StaticSourceInstances"]![0] = -2; break;
            case "gap": header["StaticSourceInstances"]![4] = 3; break;
            case "virtual-dynamic": header["StaticSourceInstances"] = JsonSerializer.SerializeToNode(new[] { 0, -1, 1, -1, 2 }); break;
        }
        var reads = 0;
        await Assert.ThrowsAsync<InvalidDataException>(async () => {
            await using var stream = await PbrSceneStream.OpenAsync(JsonSerializer.SerializeToUtf8Bytes(header), (_, _) => {
                reads++; throw new InvalidOperationException("Invalid metadata reached IO.");
            });
        });
        Assert.Equal(0, reads);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public async Task BootstrapMobilityCannotDisagreeWithHeader(int version)
    {
        var chunks = new Dictionary<string, byte[]>();
        var metadata = await PbrSceneStream.CookAsync(Scene(), (c, b, _) => {
            chunks[c.Id] = b.ToArray(); return ValueTask.CompletedTask;
        });
        var header = JsonNode.Parse(metadata)!.AsObject();
        header["Version"] = version;
        if (version == 3) header.Remove("StaticSourceInstances");
        else header["StaticSourceInstances"] = JsonSerializer.SerializeToNode(new[] { 0, 1, 2, 3, 4 });
        await Assert.ThrowsAsync<InvalidDataException>(async () => {
            await using var stream = await PbrSceneStream.OpenAsync(JsonSerializer.SerializeToUtf8Bytes(header), (c, _) =>
                ValueTask.FromResult<Stream>(new MemoryStream(chunks[c.Id], false)));
        });
    }
}
