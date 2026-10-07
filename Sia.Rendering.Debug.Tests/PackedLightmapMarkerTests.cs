using System.Runtime.InteropServices;
using Sia.Engine.Mesh;
using Sia.Engine.Rendering;
using Sia.Engine.Rendering.Pbr;
using Sia.Math;
using Xunit;

namespace Sia.Rendering.Debug.Tests;

public sealed class PackedLightmapMarkerTests
{
    [Theory]
    [InlineData(3653, 370851, 12)]
    [InlineData(4096, 524287, 12)]
    [InlineData(4096, 524288, 0)]
    [InlineData(4097, 524287, 0)]
    [InlineData(1, 1073741823, 1)]
    [InlineData(1, 1073741824, 0)]
    [InlineData(int.MaxValue, 1, 0)]
    [InlineData(0, 1, 0)]
    [InlineData(1, 0, 0)]
    public void PackingIsSelectedOnlyWhenEveryOwnerAndOneBasedMarkerFits(int owners, int markers, int expected)
        => Assert.Equal(expected, PbrResidentGeometry.PackedLightmapOwnerBits(owners, markers));

    [Fact]
    public void FullBoundaryUsesEveryBitWithoutAliasingOwnerMarkerOrHandedness()
    {
        Assert.Equal(0x7fffffffu, PbrResidentGeometry.PackLightmapOwner(4095, 524287, false, 12));
        Assert.Equal(uint.MaxValue, PbrResidentGeometry.PackLightmapOwner(4095, 524287, true, 12));
        Assert.Equal(0x80000fffu, PbrResidentGeometry.PackLightmapOwner(4095, 0, true, 12));
        Assert.Throws<ArgumentOutOfRangeException>(() => PbrResidentGeometry.PackLightmapOwner(4096, 1, false, 12));
        Assert.Throws<ArgumentOutOfRangeException>(() => PbrResidentGeometry.PackLightmapOwner(0, 524288, false, 12));
        Assert.Throws<ArgumentOutOfRangeException>(() => PbrResidentGeometry.PackLightmapOwner(0, 1, false, 31));
        Assert.Equal(48ul, PbrGpuScene.CompactVertexBytes(1, true, 12));
        Assert.Equal(80ul, PbrGpuScene.CompactVertexBytes(2, true, 12));
        Assert.Equal(112ul, PbrGpuScene.CompactVertexBytes(3, true, 12));
        Assert.Equal(144ul, PbrGpuScene.CompactVertexBytes(4, true, 12));
        Assert.Equal(192ul, PbrGpuScene.CompactVertexBytes(5, true, 12));
    }

    private static MeshVertex[] Triangle() => [
        new(new(-1, 0, 2), new(.1f, .2f, 1), new(1e9f, -.5f)) { Tangent = new(1, .1f, .2f, -1), LightmapUV = new(.1f, .1f) },
        new(new(1, 0, 2), new(.2f, .1f, 1), new(-1e9f, .5f)) { Tangent = new(1, .2f, .1f, 1), LightmapUV = new(.9f, .1f) },
        new(new(0, 1, 2), new(.1f, .1f, 1), new(0, 1)) { Tangent = new(1, .1f, .1f, -1), LightmapUV = new(.1f, .9f) }
    ];

    private static void EqualAttributes(ReadOnlySpan<uint> original, ReadOnlySpan<uint> packed, int count)
    {
        Assert.Equal(original[..(count * 4)].ToArray(), packed[..(count * 4)].ToArray());
        for (var v = 0; v < count; v++) {
            var at = count * 4 + v * 4;
            Assert.Equal(original.Slice(at, 3).ToArray(), packed.Slice(at, 3).ToArray());
            Assert.Equal(original[at + 3] & 0x80000000u, packed[at + 3] & 0x80000000u);
            Assert.Equal(original[at + 3] & 0x7fffffffu, packed[at + 3] & 4095u);
            Assert.Equal(original[count * 8 + v * 2 + 1], (packed[at + 3] & 0x7fffffffu) >> 12);
            Assert.Equal(original[count * 8 + v * 2], packed[count * 8 + v]);
        }
    }

    [Fact]
    public void ResidentPackingPreservesAllAttributeBitsAndPartialBatchNeighbors()
    {
        var tree = MeshPatchTree.Create([new(new(Triangle(), [0, 1, 2], new(new(-1, 0, 2), new(1, 1, 2))), 0, [])]);
        using var geometry = new PbrResidentGeometry(tree);
        var count = geometry.VertexCount;
        var original = new uint[count * 10]; var packed = new uint[count * 9];
        var mapping = new float4(.5f, .25f, .25f, .75f);
        geometry.PackCompactVertices(original, 0, count, float4x4.identity, float4x4.identity, 3652, count, mapping, 370850);
        geometry.PackCompactVertices(packed, 0, count, float4x4.identity, float4x4.identity, 3652, count, mapping, 370850,
            lightmapWordStride: 1, lightmapOwnerBits: 12);
        EqualAttributes(original, packed, count);
        var batched = Enumerable.Repeat(0xdeadbeefu, (count + 2) * 9).ToArray();
        for (var first = 0; first < count; first++)
            geometry.PackCompactVertices(batched, first, 1, float4x4.identity, float4x4.identity, 3652, count + 2, mapping, 370850,
                destinationFirst: first + 1, lightmapWordStride: 1, lightmapOwnerBits: 12);
        for (var plane = 0; plane < 3; plane++) {
            var words = plane == 2 ? 1 : 4;
            var offset = plane * (count + 2) * 4;
            Assert.Equal(packed.AsSpan(plane * count * 4, count * words).ToArray(), batched.AsSpan(offset + words, count * words).ToArray());
            Assert.All(batched.AsSpan(offset, words).ToArray(), v => Assert.Equal(0xdeadbeefu, v));
            Assert.All(batched.AsSpan(offset + (count + 1) * words, words).ToArray(), v => Assert.Equal(0xdeadbeefu, v));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StreamPackingPreservesBitsWithMixedHandednessAndLocalOrAtlasCoordinates(bool local)
    {
        var page = StreamGeometryPage.Cook(Triangle(), [0, 1, 2]);
        var receiver = new PbrLightmapReceiver(-1, 0, 0, 0, 16, new(.5f, .5f, .25f, .25f));
        var original = page.Vertices.ToArray(); var packed = page.Vertices.ToArray();
        PbrGpuScene.PackStreamLightmapVertices(original, page, receiver, [], [], 370850, 3652, local);
        PbrGpuScene.PackStreamLightmapVertices(packed, page, receiver, [], [], 370850, 3652, local, 12);
        EqualAttributes(MemoryMarshal.Cast<float4, uint>(original), MemoryMarshal.Cast<float4, uint>(packed), page.VertexCount);
    }

    [Fact]
    public void PackedStreamStillRejectsTrianglesCrossingCharts()
    {
        var page = StreamGeometryPage.Cook(Triangle(), [0, 1, 2]);
        PbrLightmapChart[] charts = [new(0, 0, 0, 8, 16), new(0, 8, 0, 8, 16)];
        Assert.Throws<ArgumentException>(() => PbrGpuScene.PackStreamLightmapVertices(page.Vertices.ToArray(), page,
            new(-1, 0, 0, 0, 16, new(1, 1, 0, 0)), charts, [100, 101], 0, 7, false, 12));
    }
}
