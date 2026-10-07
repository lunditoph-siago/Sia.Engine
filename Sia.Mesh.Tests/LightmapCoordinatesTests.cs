using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Sia.Engine.Mesh;
using Sia.Math;
using Xunit;

namespace Sia.Mesh.Tests;

public sealed class LightmapCoordinatesTests
{
    private static MeshData Grid(int segments, bool lightmap)
    {
        var vertices = new MeshVertex[(segments + 1) * (segments + 1)];
        var indices = new List<uint>();
        for (var y = 0; y <= segments; y++)
            for (var x = 0; x <= segments; x++) {
                var p = new float3((float)x / segments, (float)y / segments, 0);
                vertices[y * (segments + 1) + x] = new(p, new(0, 0, 1), new(p.x * 5, p.y * 7)) {
                    Tangent = new(1, 0, 0, 1),
                    LightmapUV = lightmap ? new(.1f + .4f * p.x, .15f + .7f * p.y) : default
                };
                if (x == segments || y == segments) continue;
                var a = (uint)(y * (segments + 1) + x);
                var b = a + 1;
                var c = a + (uint)segments + 1;
                indices.AddRange([a, b, c + 1, a, c + 1, c]);
            }
        return new(vertices, indices.ToArray(), new(float3.zero, new(1, 1, 0)));
    }

    [Fact]
    public void IndependentCoordinatesSurviveSimplificationAndCodec()
    {
        Assert.Equal(64, Marshal.SizeOf<MeshVertex>());
        var mesh = Grid(8, true);
        var asset = MeshPatchAsset.Cook(mesh, new(8, 4, .5f, 1, 1));
        Assert.True(asset.HasLightmapUV);
        Assert.True(asset.Build.SimplificationCount > 0);
        Assert.True(asset.ExtractRoots().Build.Tree.FinestTriangleCount < asset.Build.Tree.FinestTriangleCount);
        var bytes = asset.Encode();
        Assert.True(bytes.AsSpan(0, 8).SequenceEqual("SIAPATC3"u8));
        var restored = MeshPatchAsset.Decode(bytes);
        Assert.True(MeshPatchAsset.Decode(asset.EncodeCompressed()).Build.Tree.Vertices.SequenceEqual(asset.Build.Tree.Vertices));
        Assert.True(restored.HasLightmapUV);
        Assert.Equal(asset.SourceHash, restored.SourceHash);
        Assert.True(restored.Build.Tree.Vertices.SequenceEqual(asset.Build.Tree.Vertices));
        foreach (var v in restored.Build.Tree.Vertices) {
            Assert.Equal(.1f + .4f * v.Position.x, v.LightmapUV.x, 6);
            Assert.Equal(.15f + .7f * v.Position.y, v.LightmapUV.y, 6);
            Assert.Equal(v.Position.x * 5, v.UV.x, 6);
            Assert.Equal(v.Position.y * 7, v.UV.y, 6);
            Assert.Equal(new float4(1, 0, 0, 1), v.Tangent);
        }
        var tree = restored.Build.Tree;
        foreach (var node in tree.Nodes.Span)
            for (var t = node.TriangleOffset; t < node.TriangleOffset + node.TriangleCount; t++) {
                var a = tree.Vertices[(int)tree.Indices[t * 3]].LightmapUV;
                var b = tree.Vertices[(int)tree.Indices[t * 3 + 1]].LightmapUV;
                var c = tree.Vertices[(int)tree.Indices[t * 3 + 2]].LightmapUV;
                Assert.True((b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x) > 0);
            }
        Assert.True(restored.ExtractFinest().HasLightmapUV);
        Assert.True(restored.ExtractRoots().HasLightmapUV);
    }

    [Fact]
    public void MaterialAndLightmapCoordinatesDoNotOverwriteTangentsInEitherAssignmentOrder()
    {
        var v = new MeshVertex(new(1, 2, 3), new(0, 0, 1), new(4, 5)) {
            Tangent = new(1, 0, 0, -1), LightmapUV = new(.2f, .7f)
        };
        Assert.Equal(new float4(1, 0, 0, -1), v.Tangent);
        Assert.Equal(new float2(4, 5), v.UV);
        v = v with { UV = new(6, 7) };
        Assert.Equal(new float2(.2f, .7f), v.LightmapUV);
        v = v with { Tangent = new(0, 1, 0, 1) };
        Assert.Equal(new float2(.2f, .7f), v.LightmapUV);
        Assert.Equal(new float2(6, 7), v.UV);
        var bytes = MemoryMarshal.AsBytes(new[] { v }.AsSpan());
        Assert.Equal(6f, BitConverter.ToSingle(bytes.Slice(MeshVertex.UVOffset, 4)));
        Assert.Equal(.2f, BitConverter.ToSingle(bytes.Slice(MeshVertex.LightmapUVOffset, 4)));
        Assert.Equal(1f, BitConverter.ToSingle(bytes.Slice(MeshVertex.TangentOffset + 4, 4)));
    }

    [Fact]
    public void LightmapSeamsAreNotWeldedWhenMaterialUvMatches()
    {
        var mesh = Grid(1, false);
        var source = mesh.Vertices;
        var vertices = mesh.Indices.Select(i => source[i] with { UV = default }).ToArray();
        for (var i = 0; i < vertices.Length; i++) {
            var p = vertices[i].Position;
            vertices[i] = vertices[i] with { LightmapUV = new(p.x * .2f + (i < 3 ? .1f : .6f), p.y * .3f + .1f) };
        }
        var asset = MeshPatchAsset.Cook(new(vertices, [0, 1, 2, 3, 4, 5], mesh.Bounds), new(1, 2, .5f, 1, 1));
        var finest = asset.ExtractFinest().Build.Tree.Vertices.ToArray();
        Assert.Equal(6, finest.Length);
        Assert.Equal(2, finest.Count(v => v.Position.Equals(float3.zero)));
        Assert.Equal(2, finest.Where(v => v.Position.Equals(float3.zero)).Select(v => v.LightmapUV.x).Distinct().Count());
    }

    [Fact]
    public void LegacyEncodingAndLightmapSourceIdentityRemainDistinct()
    {
        var legacy = MeshPatchAsset.Cook(Grid(2, false));
        var bytes = legacy.Encode();
        Assert.True(bytes.AsSpan(0, 8).SequenceEqual("SIAPATC2"u8));
        var restored = MeshPatchAsset.Decode(bytes);
        Assert.False(restored.HasLightmapUV);
        Assert.Equal(bytes, restored.Encode());
        Assert.All(restored.Build.Tree.Vertices.ToArray(), v => Assert.Equal(default(float2), v.LightmapUV));
        var mapped = MeshPatchAsset.Cook(Grid(2, true));
        Assert.NotEqual(legacy.SourceHash, mapped.SourceHash);
        var changed = Grid(2, true);
        changed.Vertices[0] = changed.Vertices[0] with { LightmapUV = new(.11f, .15f) };
        Assert.NotEqual(mapped.SourceHash, MeshPatchAsset.Cook(changed).SourceHash);
    }

    [Fact]
    public void InvalidCoordinatesAndUnknownVersionsAreRejected()
    {
        var mesh = Grid(1, true);
        mesh.Vertices[0] = mesh.Vertices[0] with { LightmapUV = new(float.NaN, 0) };
        Assert.Throws<ArgumentException>(() => MeshPatchAsset.Cook(mesh));
        var bytes = MeshPatchAsset.Cook(Grid(1, true)).Encode();
        var vertexOffset = checked((int)BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(168)));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(vertexOffset + 48), BitConverter.SingleToInt32Bits(float.NaN));
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(bytes.AsSpan(0, 48));
        hash.AppendData(bytes.AsSpan(80));
        hash.GetHashAndReset().CopyTo(bytes, 48);
        Assert.Throws<InvalidDataException>(() => MeshPatchAsset.Decode(bytes));
        bytes[7] = (byte)'4';
        Assert.Throws<InvalidDataException>(() => MeshPatchAsset.Decode(bytes));
    }
}
