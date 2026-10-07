using Sia.Engine.Mesh;
using Sia.Engine.Rendering.Pbr;
using Sia.Math;
using Xunit;

namespace Sia.Rendering.Debug.Tests;

public sealed class ResidentVertexPackingTests
{
    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public void CompactAttributesPreservePositionsMaterialUvIdentityAndBoundDirectionAndBakeUvError(int lightmapStride)
    {
        var random = new Random(1837);
        float Value() => (float)(random.NextDouble() * 2 - 1);
        var vertices = Enumerable.Range(0, 300).Select(i => new MeshVertex(
            new(i % 3, i / 3, Value()), new(Value(), Value(), Value()), new(Value() * 1e10f, Value())) {
            Tangent = new(Value(), Value(), Value(), i % 2 == 0 ? -1 : 1),
            LightmapUV = new((float)random.NextDouble(), (float)random.NextDouble())
        }).ToArray();
        vertices[0] = vertices[0] with { Normal = default, Tangent = new(0, 0, 0, -1) };
        var tree = MeshPatchTree.Create([new(new(vertices, Enumerable.Range(0, vertices.Length).Select(i => (uint)i).ToArray(),
            new(new(0, 0, -1), new(2, 100, 1))), 0, [])]);
        using var geometry = new PbrResidentGeometry(tree);
        var count = geometry.VertexCount;
        var legacy = new float4[count * 4];
        var packed = new uint[count * (8 + lightmapStride)];
        var mapping = new float4(.5f, .25f, .25f, .75f);
        geometry.PackVertices(legacy, 0, count, float4x4.identity, float4x4.identity, 0,
            lightmapScaleBias: mapping, lightmapReceiver: 7);
        geometry.PackCompactVertices(packed, 0, count, float4x4.identity, float4x4.identity, 0x70000001,
            count, mapping, 7, lightmapWordStride: lightmapStride);
        for (var i = 0; i < count; i++) {
            for (var c = 0; c < 3; c++) Assert.Equal(BitConverter.SingleToUInt32Bits(legacy[i][c]), packed[i * 4 + c]);
            for (var c = 0; c < 2; c++) Assert.Equal(BitConverter.SingleToUInt32Bits(legacy[count + i][c + 2]), packed[count * 4 + i * 4 + c]);
            Assert.Equal(0x70000001u, packed[count * 4 + i * 4 + 3] & 0x7fffffffu);
            Assert.Equal(legacy[count * 2 + i].w < 0, (packed[count * 4 + i * 4 + 3] & 0x80000000u) != 0);
            AssertDirection(new(legacy[i].w, legacy[count + i].xy), DecodeDirection(packed[i * 4 + 3]));
            AssertDirection(legacy[count * 2 + i].xyz, DecodeDirection(packed[count * 4 + i * 4 + 2]));
            var uv = packed[count * 8 + i * lightmapStride];
            Assert.InRange(MathF.Abs((uv & 65535) / 65535f - legacy[count * 3 + i].x), 0, .5f / 65535 + 1e-7f);
            Assert.InRange(MathF.Abs((uv >> 16) / 65535f - legacy[count * 3 + i].y), 0, .5f / 65535 + 1e-7f);
            Assert.Equal(8u, packed[count * 8 + i * lightmapStride + 1]);
            if (lightmapStride == 4) {
                Assert.Equal(0u, packed[count * 8 + i * 4 + 2]);
                Assert.Equal(0u, packed[count * 8 + i * 4 + 3]);
            }
        }
        // Exercise partial batches and an instance starting in the middle of all three differently-sized planes.
        var batched = Enumerable.Repeat(0xdeadbeefu, (count + 2) * (8 + lightmapStride)).ToArray();
        geometry.PackCompactVertices(batched, 0, 137, float4x4.identity, float4x4.identity, 0x70000001,
            count + 2, mapping, 7, destinationFirst: 1, lightmapWordStride: lightmapStride);
        geometry.PackCompactVertices(batched, 137, count - 137, float4x4.identity, float4x4.identity, 0x70000001,
            count + 2, mapping, 7, destinationFirst: 138, lightmapWordStride: lightmapStride);
        for (var plane = 0; plane < 3; plane++) {
            var words = plane == 2 ? lightmapStride : 4;
            Assert.Equal(packed.AsSpan(plane * count * 4, count * words).ToArray(),
                batched.AsSpan(plane * (count + 2) * 4 + words, count * words).ToArray());
            Assert.All(batched.AsSpan(plane * (count + 2) * 4, words).ToArray(), v => Assert.Equal(0xdeadbeefu, v));
            Assert.All(batched.AsSpan(plane * (count + 2) * 4 + (count + 1) * words, words).ToArray(), v => Assert.Equal(0xdeadbeefu, v));
        }
    }

    [Fact]
    public void MetadataBeginsAfterAnAlignedCompleteLightmapPairAndFullBistroFitsUnchangedBudget()
    {
        Assert.Equal(32ul, PbrGpuScene.CompactVertexBytes(1, false));
        Assert.Equal(48ul, PbrGpuScene.CompactVertexBytes(1, true));
        Assert.Equal(80ul, PbrGpuScene.CompactVertexBytes(2, true));
        Assert.Equal(128ul, PbrGpuScene.CompactVertexBytes(3, true));
        var geometry = PbrGpuScene.CompactVertexBytes(4138613, true);
        Assert.Equal(165544528ul, geometry);
        Assert.True(geometry + 12297680 + 80694528 < 256ul * 1024 * 1024);
    }

    private static void AssertDirection(float3 source, float3 actual)
    {
        if (math.dot(source, source) == 0) Assert.Equal(float3.zero, actual);
        else Assert.InRange(math.length(math.normalize(source) - actual), 0, 0.0001f);
    }

    private static float3 DecodeDirection(uint value)
    {
        if (value == 0x80008000u) return default;
        var n = new float3((short)(value & 65535) / 32767f, (short)(value >> 16) / 32767f, 0);
        n.z = 1 - MathF.Abs(n.x) - MathF.Abs(n.y);
        if (n.z < 0) n.xy = new((1 - MathF.Abs(n.y)) * (n.x >= 0 ? 1 : -1),
            (1 - MathF.Abs(n.x)) * (n.y >= 0 ? 1 : -1));
        return math.normalize(n);
    }
}
