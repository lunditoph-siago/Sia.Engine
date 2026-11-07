using Sia.Asset;
using Sia.Engine.Mesh;
using Sia.Engine.Rendering;
using Sia.Math;
using Xunit;

namespace Sia.Rendering.Debug.Tests;

public sealed class StreamGeometryPackingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IdentityPackingMatchesTheOriginalScalarPathAtFiniteFloatBoundaries(bool bake)
    {
        var values = new[] { 0f, BitConverter.UInt32BitsToSingle(0x80000000), float.Epsilon, -float.Epsilon,
            BitConverter.UInt32BitsToSingle(0x007fffff), BitConverter.UInt32BitsToSingle(0x00800000),
            1f, -1f, float.MaxValue, -float.MaxValue };
        var vertices = new MeshVertex[values.Length * values.Length];
        for (var i = 0; i < vertices.Length; i++) {
            var a = values[i % values.Length]; var b = values[i / values.Length]; var c = values[(i * 3 + 1) % values.Length];
            vertices[i] = new(new(a, b, c), new(c, a, b), new(a, b)) { Tangent = new(b, c, a, values[(i + 3) % values.Length]) };
        }
        AssertOriginalWords(Page(vertices, bake), float4x4.identity);
    }

    [Theory]
    [InlineData(3, false)]
    [InlineData(17, true)]
    [InlineData(257, false)]
    [InlineData(3072, true)]
    public void IdentityPackingMatchesFiniteRandomBitsAndPreservesDestinationSlices(int count, bool bake)
    {
        var random = new Random(831 + count);
        float Next() {
            var bits = (uint)random.NextInt64(0, 1L << 32);
            if ((bits & 0x7f800000u) == 0x7f800000u) bits ^= 0x00800000u;
            return BitConverter.UInt32BitsToSingle(bits);
        }
        var vertices = new MeshVertex[count];
        for (var i = 0; i < vertices.Length; i++)
            vertices[i] = new(new(Next(), Next(), Next()), new(Next(), Next(), Next()), new(Next(), Next())) {
                Tangent = new(Next(), Next(), Next(), Next())
            };
        var page = Page(vertices, bake);
        AssertOriginalWords(page, float4x4.identity);
        foreach (var offset in new uint[] { 0, 1, 37, 1 << 23 }) {
            var storage = Enumerable.Repeat(0xdeadbeefu, page.Indices.Length + 18).ToArray();
            StreamPageArena.RelocateIndices(page.Indices, storage.AsSpan(7, page.Indices.Length), offset);
            Assert.Equal(page.Indices.ToArray().Select(index => index + offset), storage.Skip(7).Take(page.Indices.Length));
            Assert.All(storage.Take(7).Concat(storage.Skip(7 + page.Indices.Length)), value => Assert.Equal(0xdeadbeefu, value));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NonuniformTransformsKeepTheOriginalPathAndFields(bool bake)
    {
        var random = new Random(134);
        float Next() => (float)(random.NextDouble() * 8 - 4);
        var vertices = Enumerable.Range(0, 91).Select(_ => new MeshVertex(new(Next(), Next(), Next()),
            new(Next(), Next(), Next()), new(Next(), Next())) { Tangent = new(Next(), Next(), Next(), Next()) }).ToArray();
        var transform = new float4x4(new(2, .25f, 0, 0), new(0, .5f, .125f, 0), new(.2f, 0, -3, 0), new(1, -2, 4, 1));
        AssertOriginalWords(Page(vertices, bake), transform);
    }

    [Fact]
    public void SignedZeroMatrixCoefficientsKeepTheirOriginalArithmetic()
    {
        var negativeZero = BitConverter.UInt32BitsToSingle(0x80000000);
        var transform = new float4x4(new(1, negativeZero, 0, 0), new(0, 1, negativeZero, 0),
            new(negativeZero, 0, 1, 0), new(negativeZero, 0, negativeZero, 1));
        var vertices = Enumerable.Range(0, 8).Select(i => new MeshVertex(
            new((i & 1) == 0 ? 0 : negativeZero, (i & 2) == 0 ? 0 : negativeZero, (i & 4) == 0 ? 0 : negativeZero),
            new(negativeZero, 0, 1), new(negativeZero, 0)) { Tangent = new(1, negativeZero, 0, negativeZero) }).ToArray();
        AssertOriginalWords(Page(vertices, true), transform);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WholeAndTypedRasterPageRepresentationsMatchTheOriginalWords(bool bake)
    {
        var mesh = ProceduralMesh.Cube();
        var source = MeshSource.Create(mesh, bakeUvs: bake ? mesh.Vertices.Select(vertex => vertex.UV).ToArray() : []);
        var raster = MeshRasterData.Build(source, MeshRasterBuildSettings.Default with { MaximumLods = 1 });
        AssertOriginalWords(StreamGeometryPage.Create(raster, 1, 5), float4x4.identity);
        var pages = new Dictionary<AssetId, MeshRasterPageData>();
        var product = MeshRasterPagesData.Build(raster, AssetId.New(), AssetId.New(), (id, page) => pages.Add(id, page));
        var index = product.VertexPageCount;
        var dependencies = product.GetVertexDependencies(index).ToArray()
            .ToDictionary(page => page, page => pages[product.Pages[page].Id]);
        var compact = StreamGeometryPage.Create(product, index, pages[product.Pages[index].Id], dependencies);
        AssertOriginalWords(compact, float4x4.identity);
    }

    private static void AssertOriginalWords(StreamGeometryPage page, float4x4 transform)
    {
        foreach (var instance in new uint[] { 0, 1, 1 << 24 })
            foreach (var magnitude in new[] { 1f, .25f, 4096f }) {
                var expected = new uint[page.VertexCount * StreamVertexEncoding.Words];
                OriginalScalar(page, expected, transform, instance, magnitude);
                var storage = Enumerable.Repeat(0xdeadbeefu, expected.Length + 24).ToArray();
                StreamPageArena.PackVertices(page, storage.AsSpan(11, expected.Length), transform, instance, magnitude);
                Assert.Equal(expected, storage.Skip(11).Take(expected.Length));
                Assert.All(storage.Take(11).Concat(storage.Skip(11 + expected.Length)), value => Assert.Equal(0xdeadbeefu, value));
                for (var i = 0; i < page.VertexCount; i++) {
                    Assert.Equal(instance | (page.HasBakeUvs ? 0x80000000u : 0), expected[page.VertexCount * 12 + i]);
                    var tangent = page.GetVertex(i).Tangent.w;
                    Assert.Equal(BitConverter.SingleToUInt32Bits((tangent < 0 ? -1 : 1) * magnitude), expected[page.VertexCount * 8 + i * 4 + 2]);
                }
            }
    }

    // Frozen pre-optimization owner-context packing algorithm. Quantization remains
    // the unchanged public runtime encoder, independently of transform dispatch.
    private static void OriginalScalar(StreamGeometryPage page, Span<uint> packed, float4x4 transform, uint instance, float magnitude)
    {
        var normal = math.transpose(math.inverse(transform));
        for (var i = 0; i < page.VertexCount; i++) {
            var vertex = page.GetVertex(i); var t = vertex.Tangent;
            var position = math.mul(transform, new float4(vertex.Position, 1)).xyz;
            var direction = math.mul(normal, new float4(vertex.Normal, 0)).xyz;
            var value = new MeshVertex(position, direction, vertex.UV) {
                Tangent = new(math.mul(transform, new float4(t.xyz, 0)).xyz, (t.w < 0 ? -1 : 1) * magnitude)
            };
            StreamVertexEncoding.PackVertex(packed, i, page.VertexCount, value, page.GetBakeUv(i), page.HasBakeUvs, instance);
        }
    }

    private static StreamGeometryPage Page(MeshVertex[] vertices, bool bake)
    {
        var indices = Enumerable.Range(0, (vertices.Length + 2) / 3 * 3).Select(i => (uint)(i % vertices.Length)).ToArray();
        var uvs = bake ? vertices.Select(vertex => vertex.UV).ToArray() : [];
        return StreamGeometryPage.Create(MeshVirtualPageData.Create(vertices, indices, uvs));
    }
}
