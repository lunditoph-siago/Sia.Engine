using System.Buffers.Binary;
using Sia.Engine.Rendering.Pbr;
using Sia.Math;
using Xunit;

namespace Sia.Rendering.Debug.Tests;

public sealed class LightmapGpuPageTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(1764)]
    [InlineData(40494)]
    [InlineData(50000)]
    public void PageSelectionMatchesIndependentSortedOracle(int capacity)
    {
        var demand = Enumerable.Range(0, 40494).Select(i => i % 11 == 0 ? 0f
            : i % 7 == 0 ? float.MaxValue : (i * 48271 % 10397) / 4f).ToArray();
        var expected = Enumerable.Range(0, demand.Length).Where(p => demand[p] > 0)
            .OrderByDescending(p => demand[p]).ThenBy(p => p).Take(capacity).Order().ToArray();
        var wanted = new bool[demand.Length];
        var priorities = new PriorityQueue<int, (float Score, int Tie)>();
        PbrLightmapGpu.SelectWantedPages(demand, wanted, capacity, priorities);
        Assert.Equal(expected, Enumerable.Range(0, wanted.Length).Where(p => wanted[p]).ToArray());
        Assert.Equal(expected.Length, priorities.Count);
    }

    [Fact]
    public void PageSelectionPreservesTiesAndClearsReusedState()
    {
        float[] demand = [5, 5, 0, 10, 5, -1];
        var wanted = Enumerable.Repeat(true, demand.Length).ToArray();
        var priorities = new PriorityQueue<int, (float Score, int Tie)>();
        priorities.Enqueue(100, (float.MaxValue, 0));
        PbrLightmapGpu.SelectWantedPages(demand, wanted, 3, priorities);
        Assert.Equal(new[] { true, true, false, true, false, false }, wanted);
        Array.Clear(demand); demand[4] = 1;
        PbrLightmapGpu.SelectWantedPages(demand, wanted, 3, priorities);
        Assert.Equal(new[] { false, false, false, false, true, false }, wanted);
        Assert.Single(priorities.UnorderedItems);
    }

    [Fact]
    public void PageSelectionHandlesEmptyInputAndRejectsInvalidShape()
    {
        var priorities = new PriorityQueue<int, (float Score, int Tie)>();
        PbrLightmapGpu.SelectWantedPages([], [], 3, priorities);
        Assert.Empty(priorities.UnorderedItems);
        Assert.Throws<ArgumentOutOfRangeException>(() => PbrLightmapGpu.SelectWantedPages([], [], -1, priorities));
        Assert.Throws<ArgumentException>(() => PbrLightmapGpu.SelectWantedPages([1], [], 1, priorities));
    }

    [Fact]
    public async Task MetadataLayoutPreservesReceiverOrderChartOwnershipPackedBytesAndZeroMappings()
    {
        PbrLightmapReceiver[] receivers = [new(-1, 7, 0, 16, 8, default), new(-1, 2, 8, 0, 16, default)];
        PbrLightmapChart[] charts = [new(2, 8, 0, 16, 16), new(7, 0, 16, 8, 8)];
        var values = new float4[32 * 32 * 4];
        foreach (var r in receivers) for (var y = r.Y; y < r.Y + r.Resolution; y++) for (var x = r.X; x < r.X + r.Resolution; x++) {
            var at = (y * 32 + x) * 4; var radiance = new float3(r.StaticInstance * .1f, .2f, .3f);
            values[at] = new(radiance, 1); values[at + 1] = new(-radiance, 0);
            values[at + 2] = new(radiance * .5f, 0); values[at + 3] = new(float3.zero, 0);
        }
        var asset = new PbrLightmapAsset(32, receivers, new byte[32], new byte[32], new(), values, charts).Quantize();
        var chunks = new Dictionary<string, byte[]>();
        var encoded = await PbrLightmapStream.CookAsync(asset, (c, b, _) => { chunks[c.Id] = b.ToArray(); return ValueTask.CompletedTask; });
        await using var stream = await PbrLightmapStream.OpenAsync(encoded,
            (c, _) => ValueTask.FromResult<Stream>(new MemoryStream(chunks[c.Id], false)));
        Assert.Equal(new[] { 0, 4, 8 }, PbrLightmapGpu.ReceiverPageBases(stream));
        var metadata = PbrLightmapGpu.CreateMetadata(stream);
        Assert.Equal(15, PbrLightmapGpu.TableMetadataOffset(stream));
        Assert.Equal(17, metadata.Length);
        Assert.Equal(new float4(2, 2, 4, 32), metadata[0]);
        Assert.Equal(stream.DecodeScales.ToArray(), metadata.AsSpan(1, 8).ToArray());
        Assert.Equal(8u, BitConverter.SingleToUInt32Bits(metadata[9].x));
        Assert.Equal(16u | 16u << 16, BitConverter.SingleToUInt32Bits(metadata[9].y));
        Assert.Equal(3, metadata[9].z); Assert.Equal(1, metadata[9].w);
        Assert.Equal(0, metadata[10].w);
        for (var c = 0; c < 2; c++) for (var band = 0; band < 4; band++)
            Assert.Equal(BinaryPrimitives.ReadUInt32LittleEndian(stream.CoarseCoefficients.Span.Slice(c * 16 + band * 4, 4)),
                BitConverter.SingleToUInt32Bits(metadata[11 + c][band]));
        Assert.Equal(16u << 16, BitConverter.SingleToUInt32Bits(metadata[13].x));
        Assert.Equal(new float3(8, 0, 3), metadata[13].yzw);
        Assert.Equal(new float3(16, 4, 3), metadata[14].yzw);
        Assert.All(metadata.AsSpan(15).ToArray(), value => Assert.Equal(float4.zero, value));
        Assert.Equal(23136, PbrLightmapGpu.RequiredDecodedBytes);
        Assert.Equal(0, stream.Statistics.ReadBytes);
    }
}
