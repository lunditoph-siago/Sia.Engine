using Sia.Engine.Mesh;
using Sia.Engine.Rendering.Pbr;
using Sia.Math;
using Xunit;

namespace Sia.Rendering.Debug.Tests;

public sealed class ResidentGeometryCompactionTests
{
    [Fact]
    public void ConventionalCutsOmitIntermediateOnlyVerticesAndKeepEveryDrawnAttribute()
    {
        MeshData Quad(float? center) {
            var vertices = new List<MeshVertex> {
                new(new(0, 0, 0), new(0, 0, 1), new(0, 0)) { LightmapUV = new(.1f, .1f) },
                new(new(1, 0, 0), new(0, 0, 1), new(1, 0)) { LightmapUV = new(.9f, .1f) },
                new(new(1, 1, 0), new(0, 0, 1), new(1, 1)) { LightmapUV = new(.9f, .9f) },
                new(new(0, 1, 0), new(0, 0, 1), new(0, 1)) { LightmapUV = new(.1f, .9f) }
            };
            uint[] indices = [0, 1, 2, 0, 2, 3];
            if (center is { } z) {
                vertices.Add(new(new(.5f, .5f, z), new(0, 0, 1), new(.5f, .5f)) { LightmapUV = new(.5f, .5f) });
                indices = [0, 1, 4, 1, 2, 4, 2, 3, 4, 3, 0, 4];
            }
            return new(vertices.ToArray(), indices, new(float3.zero, new(1, 1, .2f)));
        }
        var tree = MeshPatchTree.Create([new(Quad(null), .2f, [new(Quad(.1f), .1f, [new(Quad(.2f), 0, [])])])]);
        Assert.Equal(6, tree.VertexCount);
        using var geometry = new PbrResidentGeometry(tree);
        Assert.Equal(5, geometry.VertexCount);
        Assert.Equal(4u, geometry.Fine);
        Assert.Equal(2u, geometry.Coarse);
        var packed = new float4[geometry.VertexCount * 4];
        geometry.PackVertices(packed, 0, geometry.VertexCount, float4x4.identity, float4x4.identity,
            material: 2, lightmapScaleBias: new(1, 1, 0, 0), lightmapReceiver: 2);
        var actual = new List<uint>();
        geometry.UploadIndices(new uint[2], 11, (values, first) => {
            Assert.Equal(actual.Count, first);
            actual.AddRange(values.ToArray());
        });
        var expected = tree.Nodes.ToArray().Where(n => n.ChildCount == 0)
            .Concat(tree.Nodes.ToArray().Take(tree.RootCount))
            .SelectMany(n => tree.Indices.Slice(n.TriangleOffset * 3, n.TriangleCount * 3).ToArray()).ToArray();
        Assert.Equal(expected.Length, actual.Count);
        for (var i = 0; i < actual.Count; i++) {
            var index = (int)actual[i] - 11;
            Assert.InRange(index, 0, geometry.VertexCount - 1);
            var source = tree.Vertices[(int)expected[i]];
            Assert.Equal(source.Position, packed[index].xyz);
            Assert.Equal(source.Normal, new(packed[index].w, packed[geometry.VertexCount + index].xy));
            Assert.Equal(source.UV, packed[geometry.VertexCount + index].zw);
            Assert.Equal(source.Tangent.xyz, packed[geometry.VertexCount * 2 + index].xyz);
            Assert.Equal((source.Tangent.w < 0 ? -1 : 1) * 3, packed[geometry.VertexCount * 2 + index].w);
            Assert.Equal(new float4(source.LightmapUV.x, source.LightmapUV.y, 3, 0), packed[geometry.VertexCount * 3 + index]);
        }
        Assert.DoesNotContain(packed.Take(geometry.VertexCount), v => v.z == .1f);
    }
}
