using Sia.Engine.Mesh;

namespace Sia.Engine.Rendering;

/// <summary>Borrowed immutable Mesh page for bounded upload staging. No asset codec, copy or identity.</summary>
public sealed class StreamGeometryPage
{
    public const int VertexPlanes = 4;
    public const int VertexBytes = VertexPlanes * 16;
    public const int MaximumBytes = MeshVirtualPageData.MaximumTriangles * 3 * (VertexBytes + sizeof(uint));
    public MeshVirtualPageData Data { get; }
    public int VertexCount => Data.Vertices.Length;
    public int TriangleCount => Data.TriangleCount;
    public int UploadBytes => checked(VertexCount * VertexBytes + Data.Indices.Length * 4);
    public ReadOnlySpan<uint> Indices => Data.Indices;
    private StreamGeometryPage(MeshVirtualPageData data) => Data = data;

    public static StreamGeometryPage Create(MeshVirtualPageData page)
    {
        ArgumentNullException.ThrowIfNull(page);
        return new(page);
    }
}
