using System.Buffers.Binary;
using Sia.Engine.Mesh;
using Sia.Engine.Rendering;
using Sia.Math;
using Xunit;

namespace Sia.Rendering.Debug.Tests;

public sealed class LightmapPageTests
{
    private static (MeshVertex[] Vertices, uint[] Indices) Triangles(int count, bool lightmap)
    {
        var vertices = new MeshVertex[count * 3];
        for (var i = 0; i < vertices.Length; i++) {
            vertices[i] = new(new(i, i % 3, 0), new(0, 0, 1), new(i % 3, 0)) {
                Tangent = new(1, 0, 0, 1), LightmapUV = lightmap ? new(.1f + i % 3 * .2f, .3f) : default
            };
        }
        return (vertices, Enumerable.Range(0, vertices.Length).Select(i => (uint)i).ToArray());
    }

    [Fact]
    public void SidecarRetainsCoordinatesWithoutChangingGpuVertexPlanes()
    {
        var (vertices, indices) = Triangles(2, true);
        var page = StreamGeometryPage.Cook(vertices, indices);
        Assert.True(page.HasLightmapUV);
        Assert.Equal(56, page.EncodedVertexBytes);
        Assert.Equal(vertices.Length * 3, page.Vertices.Length);
        Assert.Equal(indices.Length, page.Indices.Length);
        Assert.Equal(vertices.Select(v => v.LightmapUV), Enumerable.Range(0, page.VertexCount).Select(page.GetLightmapUV));
        var decoded = StreamGeometryPage.Decode(page.Bytes);
        Assert.True(decoded.Indices.SequenceEqual(indices));
        Assert.True(decoded.LightmapCoordinates.SequenceEqual(page.LightmapCoordinates));
        var legacyVertices = vertices.Select(v => v with { LightmapUV = default }).ToArray();
        var legacy = StreamGeometryPage.Cook(legacyVertices, indices);
        Assert.False(legacy.HasLightmapUV);
        Assert.Equal(48, legacy.EncodedVertexBytes);
        Assert.True(legacy.LightmapCoordinates.IsEmpty);
        Assert.True(page.Vertices.SequenceEqual(legacy.Vertices));
        Assert.True(legacy.Bytes.Span[..8].SequenceEqual("SIAPAGE\0"u8));
        Assert.True(page.Bytes.Span[..8].SequenceEqual("SIAPAGE1"u8));
    }

    [Fact]
    public void LightmapSeamAttributesRemainSeparate()
    {
        var (vertices, indices) = Triangles(1, true);
        var seam = vertices.Concat(vertices.Select(v => v with { LightmapUV = v.LightmapUV + new float2(.4f, 0) })).ToArray();
        var page = StreamGeometryPage.Cook(seam, [0, 1, 2, 3, 4, 5]);
        Assert.Equal(6, page.VertexCount);
        Assert.Equal(12, page.LightmapCoordinates.Length);
    }

    [Fact]
    public void EncodedBudgetIncludesCoordinateSidecar()
    {
        var count = (StreamGeometryPage.MaximumBytes - 16) / 180;
        var (vertices, indices) = Triangles(count, true);
        var page = StreamGeometryPage.Cook(vertices, indices);
        Assert.InRange(page.Bytes.Length, 1, StreamGeometryPage.MaximumBytes);
        var (tooMany, extraIndices) = Triangles(count + 1, true);
        Assert.Throws<ArgumentException>(() => StreamGeometryPage.Cook(tooMany, extraIndices));
    }

    [Fact]
    public void MalformedSidecarAndUnsupportedVersionAreRejected()
    {
        var (vertices, indices) = Triangles(1, true);
        var page = StreamGeometryPage.Cook(vertices, indices);
        var bytes = page.Bytes.ToArray();
        var sidecar = 16 + page.VertexCount * 48 + page.TriangleCount * 12;
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(sidecar), BitConverter.SingleToInt32Bits(float.NaN));
        Assert.Throws<InvalidDataException>(() => StreamGeometryPage.Decode(bytes));
        Assert.Throws<InvalidDataException>(() => StreamGeometryPage.Decode(page.Bytes[..^1]));
        bytes = page.Bytes.ToArray();
        bytes[7] = (byte)'2';
        Assert.Throws<InvalidDataException>(() => StreamGeometryPage.Decode(bytes));
    }
}
