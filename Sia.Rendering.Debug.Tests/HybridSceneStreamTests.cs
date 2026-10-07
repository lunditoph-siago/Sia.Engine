using System.Text.Json;
using System.Text.Json.Nodes;
using System.Runtime.InteropServices;
using Sia.Asset;
using Sia.Engine.Mesh;
using Sia.Engine.Rendering.Pbr;
using Sia.Math;
using Xunit;

namespace Sia.Rendering.Debug.Tests;

public sealed class HybridSceneStreamTests
{
    private static PbrSceneAsset Scene(float lightmapInset = .1f)
    {
        MeshPatchAsset Quad(float x) => MeshPatchAsset.Cook(new([
            new(new(x, 0, 0), new(0, 0, 1), new(0, 0)) { LightmapUV = new(lightmapInset, lightmapInset) },
            new(new(x + 1, 0, 0), new(0, 0, 1), new(1, 0)) { LightmapUV = new(.9f, .1f) },
            new(new(x + 1, 1, 0), new(0, 0, 1), new(1, 1)) { LightmapUV = new(.9f, .9f) },
            new(new(x, 1, 0), new(0, 0, 1), new(0, 1)) { LightmapUV = new(.1f, .9f) }],
            [0, 1, 2, 0, 2, 3], new(new(x, 0, 0), new(x + 1, 1, 0))));
        float4x4 Move(float x, float z) => new(new(1, 0, 0, 0), new(0, 1, 0, 0), new(0, 0, 1, 0), new(x, 0, z, 1));
        return PbrSceneAsset.Create([Quad(0), Quad(3)],
            [new(new() { BaseColor = new(.5f), EmissiveColor = new(1, 0, 0), EmissiveStrength = 2 }, DoubleSided: true),
                new(new() { BaseColor = new(1) }, AlphaBlend: true)],
            [new(0, 0, Move(-10, 2)), new(1, 0, float4x4.identity),
                new(1, 1, float4x4.identity), new(0, 0, Move(10, 1))]);
    }

    [Fact]
    public async Task ArenaShapeCountsSharedRootsOnceAndOmitsAbsentDetail()
    {
        var chunks = new Dictionary<string, byte[]>();
        var metadata = await PbrSceneStream.CookAsync(Scene(), (chunk, bytes, _) => {
            chunks[chunk.Id] = bytes.ToArray(); return ValueTask.CompletedTask;
        }, new int[] { 1 });
        await using var stream = await PbrSceneStream.OpenAsync(metadata, (chunk, _) =>
            ValueTask.FromResult<Stream>(new MemoryStream(chunks[chunk.Id], false)));
        Assert.Equal(2, stream.Instances.Length);
        Assert.Equal((4ul, 2ul, 0ul, 0ul), PbrGpuScene.StreamGeometryShape(stream, true, true));
        Assert.Equal((4ul, 2ul, 0ul, 0ul), PbrGpuScene.StreamGeometryShape(stream, true, false));
        Assert.Equal((8ul, 4ul, 0ul, 0ul), PbrGpuScene.StreamGeometryShape(stream, false, true));
    }

    [Fact]
    public async Task MixedCookKeepsAllInstancesOfSelectedAssetsAndCanonicalTransport()
    {
        var source = Scene();
        var chunks = new Dictionary<string, byte[]>();
        var metadata = await PbrSceneStream.CookAsync(source, (chunk, bytes, _) => {
            chunks.Add(chunk.Id, bytes.ToArray());
            return ValueTask.CompletedTask;
        }, new int[] { 0 });
        Assert.Equal(4, JsonNode.Parse(metadata)!["Version"]!.GetValue<int>());
        await using var stream = await PbrSceneStream.OpenAsync(metadata, (chunk, _) =>
            ValueTask.FromResult<Stream>(new MemoryStream(chunks[chunk.Id], false)));
        Assert.Equal(new[] { 1 }, stream.OpaqueSourceInstances.ToArray());
        Assert.Equal(new[] { 0, 2, 3 }, stream.BootstrapSourceInstances.ToArray());
        Assert.Equal(3, stream.Bootstrap.Instances.Length);
        Assert.All(stream.Bootstrap.Geometry.ToArray(), g => Assert.True(g.HasLightmapUV));
        Assert.Equal(PbrSceneTransport.Identity(source), stream.Identity.ToArray());
        Assert.Equal(PbrLightmapAsset.Identity(source), stream.LightmapIdentity.ToArray());
        var changedCoordinates = Scene(.2f);
        Assert.Equal(PbrSceneTransport.StaticIdentity(source), PbrSceneTransport.StaticIdentity(changedCoordinates));
        Assert.NotEqual(stream.LightmapIdentity.ToArray(), PbrLightmapAsset.Identity(changedCoordinates));
        var expected = PbrSceneTransport.BuildStatic(source);
        var actual = PbrSceneTransport.BuildStatic(stream);
        Assert.Equal(6, actual.TriangleCount);
        Assert.Equal(expected.Bounds, actual.Bounds);
        Assert.Equal(expected.Identity.ToArray(), actual.Identity.ToArray());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    [InlineData(0, 0)]
    [InlineData(0, 1)]
    public async Task InvalidSelectionFailsBeforeWriting(params int[] selection)
    {
        var writes = 0;
        await Assert.ThrowsAsync<ArgumentException>(() => PbrSceneStream.CookAsync(Scene(), (_, _, _) => {
            writes++;
            return ValueTask.CompletedTask;
        }, selection));
        Assert.Equal(0, writes);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public async Task MixedBootstrapCannotClaimAnOlderOrUnknownVersion(int version)
    {
        var chunks = new Dictionary<string, byte[]>();
        var metadata = await PbrSceneStream.CookAsync(Scene(), (chunk, bytes, _) => {
            chunks.Add(chunk.Id, bytes.ToArray());
            return ValueTask.CompletedTask;
        }, new int[] { 0 });
        var header = JsonNode.Parse(metadata)!.AsObject();
        header["Version"] = version;
        header.Remove("LightmapIdentity");
        header.Remove("StaticSourceInstances");
        var reads = 0;
        await Assert.ThrowsAsync<InvalidDataException>(async () => {
            await using var stream = await PbrSceneStream.OpenAsync(JsonSerializer.SerializeToUtf8Bytes(header), (chunk, _) => {
                reads++;
                return ValueTask.FromResult<Stream>(new MemoryStream(chunks[chunk.Id], false));
            });
        });
        if (version == 5) Assert.Equal(0, reads);
        else Assert.True(reads > 0); // The downgrade becomes invalid when its checked bootstrap is decoded.
    }

    [Theory]
    [InlineData("missing-identity")]
    [InlineData("short-identity")]
    [InlineData("missing-slot")]
    [InlineData("duplicate-slot")]
    [InlineData("out-of-range-slot")]
    public async Task InvalidLightmapSourceContractFailsBeforeChunkIo(string mutation)
    {
        var metadata = await PbrSceneStream.CookAsync(Scene(), (_, _, _) => ValueTask.CompletedTask, new int[] { 0 });
        var header = JsonNode.Parse(metadata)!.AsObject();
        switch (mutation) {
            case "missing-identity": header.Remove("LightmapIdentity"); break;
            case "short-identity": header["LightmapIdentity"] = Convert.ToBase64String(new byte[31]); break;
            case "missing-slot": header["SourceInstanceCount"] = 5; break;
            case "duplicate-slot": header["BootstrapSourceInstances"]![0] = 1; break;
            case "out-of-range-slot": header["BootstrapSourceInstances"]![0] = 4; break;
        }
        var reads = 0;
        await Assert.ThrowsAsync<InvalidDataException>(async () => {
            await using var stream = await PbrSceneStream.OpenAsync(JsonSerializer.SerializeToUtf8Bytes(header), (_, _) => {
                reads++;
                throw new InvalidOperationException("Invalid metadata must not open chunks.");
            });
        });
        Assert.Equal(0, reads);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task OlderStreamsRemainReadableButRequireRecookingForLightmapIdentity(int version)
    {
        var chunks = new Dictionary<string, byte[]>();
        var metadata = await PbrSceneStream.CookAsync(Scene(), (chunk, bytes, _) => {
            chunks.Add(chunk.Id, bytes.ToArray());
            return ValueTask.CompletedTask;
        });
        var header = JsonNode.Parse(metadata)!.AsObject();
        header["Version"] = version;
        header.Remove("LightmapIdentity");
        header.Remove("StaticSourceInstances");
        await using var stream = await PbrSceneStream.OpenAsync(JsonSerializer.SerializeToUtf8Bytes(header), (chunk, _) =>
            ValueTask.FromResult<Stream>(new MemoryStream(chunks[chunk.Id], false)));
        Assert.True(stream.LightmapIdentity.IsEmpty);
        Assert.Equal(PbrSceneTransport.StaticIdentity(Scene()), stream.StaticIdentity.ToArray());
    }

    [Fact]
    public async Task LightmapDomainRequiresEveryVirtualAndConventionalReceiverAndRejectsTransparentSlots()
    {
        var source = Scene();
        var chunks = new Dictionary<string, byte[]>();
        var metadata = await PbrSceneStream.CookAsync(source, (c, b, _) => {
            chunks[c.Id] = b.ToArray(); return ValueTask.CompletedTask;
        }, new int[] { 0 });
        await using var stream = await PbrSceneStream.OpenAsync(metadata, (c, _) =>
            ValueTask.FromResult<Stream>(new MemoryStream(chunks[c.Id], false)));
        var receivers = new[] { 0, 1, 3 }.Select(i => new PbrLightmapReceiver(-1, i, 0, 0, 8, default)).ToArray();
        var identity = PbrLightmapAsset.Identity(source);
        PbrGpuScene.ValidateLightmapSource(stream.Bootstrap, stream, identity, receivers);
        Assert.Throws<ArgumentException>(() => PbrGpuScene.ValidateLightmapSource(stream.Bootstrap, stream, identity, receivers[..2]));
        Assert.Throws<ArgumentException>(() => PbrGpuScene.ValidateLightmapSource(stream.Bootstrap, stream, identity,
            [.. receivers, new(-1, 2, 0, 0, 8, default)]));
        Assert.Throws<ArgumentException>(() => PbrGpuScene.ValidateLightmapSource(stream.Bootstrap, stream,
            PbrLightmapAsset.Identity(Scene(.2f)), receivers));
    }

    [Fact]
    public async Task SharedPageUsesLocalChartOrdinalAndPerInstanceAtlasLookup()
    {
        var source = Scene();
        var chunks = new Dictionary<string, byte[]>();
        var metadata = await PbrSceneStream.CookAsync(source, (c, b, _) => {
            chunks[c.Id] = b.ToArray(); return ValueTask.CompletedTask;
        }, new int[] { 1 });
        await using var stream = await PbrSceneStream.OpenAsync(metadata, (c, _) =>
            ValueTask.FromResult<Stream>(new MemoryStream(chunks[c.Id], false)));
        PbrLightmapReceiver[] receivers = [new(-1, 0, 0, 0, 8, default), new(-1, 1, 8, 0, 8, default), new(-1, 3, 16, 0, 8, default)];
        PbrLightmapChart[] charts = [new(3, 16, 0, 8, 8), new(1, 8, 0, 8, 8), new(0, 0, 0, 8, 8)];
        var field = new PbrLightmapAsset(32, receivers, PbrLightmapAsset.Identity(source), new byte[32], new(), new float4[32 * 32 * 4], charts);
        var shared = PbrGpuScene.CreateStreamLightmapMetadata(stream, field, null, true);
        var descriptor = (int)BitConverter.SingleToUInt32Bits(shared.Metadata[0].x);
        Assert.Equal(field.Receivers.Span[0].ScaleBias, shared.Metadata[descriptor]);
        Assert.Equal(field.Receivers.Span[2].ScaleBias, shared.Metadata[descriptor + 2]);
        var lookup = (int)BitConverter.SingleToUInt32Bits(shared.Metadata[descriptor + 1].w);
        var lookupWords = MemoryMarshal.Cast<float4, uint>(shared.Metadata.AsSpan(lookup)).ToArray();
        Assert.Equal(new uint[] { 3, 1 }, lookupWords[..2]);
        var page = stream.ResidentRoots.Values.First();
        var a = page.Vertices.ToArray(); var b = page.Vertices.ToArray();
        shared.Pack(a, page, 0); shared.Pack(b, page, 1);
        Assert.Equal(MemoryMarshal.Cast<float4, uint>(a).ToArray(), MemoryMarshal.Cast<float4, uint>(b).ToArray());
        Assert.Equal(1u, MemoryMarshal.Cast<float4, uint>(a)[page.VertexCount * 8 + 1]);
        var world = PbrGpuScene.CreateStreamLightmapMetadata(stream, field, null, false);
        a = page.Vertices.ToArray(); b = page.Vertices.ToArray();
        world.Pack(a, page, 0); world.Pack(b, page, 1);
        var aWords = MemoryMarshal.Cast<float4, uint>(a).ToArray(); var bWords = MemoryMarshal.Cast<float4, uint>(b).ToArray();
        Assert.Equal(3u, aWords[page.VertexCount * 8 + 1]);
        Assert.Equal(1u, bWords[page.VertexCount * 8 + 1]);
        Assert.NotEqual(aWords[page.VertexCount * 8], bWords[page.VertexCount * 8]);
        // CPU-selected High pages retain different instance owners even with equal materials.
        var instanced = PbrGpuScene.CreateStreamLightmapMetadata(stream, field, null, false, localInstances: true);
        a = page.Vertices.ToArray(); b = page.Vertices.ToArray();
        instanced.Pack(a, page, 0); instanced.Pack(b, page, 1);
        Assert.Equal(0u, MemoryMarshal.Cast<float4, uint>(a)[page.VertexCount * 4 + 3] & 0x7fffffffu);
        Assert.Equal(1u, MemoryMarshal.Cast<float4, uint>(b)[page.VertexCount * 4 + 3] & 0x7fffffffu);
        receivers[2] = receivers[2] with { Resolution = 16 };
        charts[0] = charts[0] with { Width = 16, Height = 16 };
        var incompatible = new PbrLightmapAsset(32, receivers, field.SceneIdentity.Span, new byte[32], new(), new float4[32 * 32 * 4], charts);
        Assert.Throws<ArgumentException>(() => PbrGpuScene.CreateStreamLightmapMetadata(stream, incompatible, null, true));
        Assert.Throws<ArgumentException>(() => PbrGpuScene.PackStreamLightmapVertices(page.Vertices.ToArray(), page, field.Receivers.Span[0],
            [new(0, 0, 0, 4, 8), new(0, 4, 0, 4, 8)], [0, 1], 0, 0, true));
    }
}
