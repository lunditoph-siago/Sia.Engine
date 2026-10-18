using System.Runtime.InteropServices;
using Sia.Engine.Mesh;
using Sia.Math;

namespace Sia.Engine.Rendering;

/// <summary>Transient GPU upload packing of an acquired page. This is not an asset codec or identity.</summary>
public sealed class StreamGeometryPage
{
    public const int MaximumBytes = MeshVirtualPageData.MaximumTriangles * 3 * 52;
    public ReadOnlyMemory<byte> Bytes { get; }
    public int VertexCount { get; }
    public int TriangleCount { get; }
    public ReadOnlySpan<float4> Vertices => MemoryMarshal.Cast<byte, float4>(Bytes.Span[..(VertexCount * 48)]);
    public ReadOnlySpan<uint> Indices => MemoryMarshal.Cast<byte, uint>(Bytes.Span[(VertexCount * 48)..]);
    private StreamGeometryPage(byte[] bytes, int vertices, int triangles) => (Bytes, VertexCount, TriangleCount) = (bytes, vertices, triangles);

    public static StreamGeometryPage Create(MeshVirtualPageData page)
    {
        ArgumentNullException.ThrowIfNull(page);
        var count = page.Vertices.Length;
        var bytes = new byte[checked(count * 48 + page.Indices.Length * 4)];
        var packed = MemoryMarshal.Cast<byte, float4>(bytes.AsSpan(0, count * 48));
        for (var i = 0; i < count; i++) {
            var v = page.Vertices[i];
            packed[i] = new(v.Position, v.Normal.x);
            packed[count + i] = new(v.Normal.y, v.Normal.z, v.UV.x, v.UV.y);
            packed[count * 2 + i] = v.Tangent;
        }
        MemoryMarshal.AsBytes(page.Indices).CopyTo(bytes.AsSpan(count * 48));
        return new(bytes, count, page.TriangleCount);
    }
}
