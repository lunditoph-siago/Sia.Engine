using Sia.Engine.Mesh;
using Sia.Math;

namespace Sia.Engine.Rendering;

/// <summary>Borrowed immutable Mesh page for bounded upload staging. No asset codec, copy or identity.</summary>
public sealed class StreamGeometryPage
{
    public const int VertexPlanes = 4;
    public const int VertexBytes = StreamVertexEncoding.Words * sizeof(uint);
    public static uint PlaneStride(uint plane) => plane < VertexPlanes
        ? plane == 3 ? 4u : 16u : throw new ArgumentOutOfRangeException(nameof(plane));
    public const int MaximumBytes = MeshVirtualPageData.MaximumTriangles * 3 * (VertexBytes + sizeof(uint));
    public MeshVirtualPageData? Data { get; }
    private readonly MeshRasterData? _raster;
    private readonly int[]? _source;
    private readonly uint[]? _indices;
    public int VertexCount => Data?.Vertices.Length ?? _source!.Length;
    public int TriangleCount => Indices.Length / 3;
    public bool HasBakeUvs => Data?.HasBakeUvs ?? _raster!.HasBakeUvs;
    public int UploadBytes => checked(VertexCount * VertexBytes + Indices.Length * 4);
    public ReadOnlySpan<uint> Indices => Data is { } data ? data.Indices : _indices;
    public MeshVertex GetVertex(int index) => Data is { } data ? data.Vertices[index] : _raster!.Vertices[_source![index]];
    public float2 GetBakeUv(int index) => !HasBakeUvs ? default : Data is { } data ? data.BakeUvs[index] : _raster!.BakeUvs[_source![index]];
    private StreamGeometryPage(MeshVirtualPageData data) => Data = data;

    public static StreamGeometryPage Create(MeshVirtualPageData page)
    {
        ArgumentNullException.ThrowIfNull(page);
        return new(page);
    }

    private StreamGeometryPage(MeshRasterData raster, int[] source, uint[] indices)
        => (_raster, _source, _indices) = (raster, source, indices);

    /// <summary>Bounded upload batch borrowing exact Raster vertices. Owns only compact index/remap rows; no virtual cooking.</summary>
    public static StreamGeometryPage Create(MeshRasterData raster, int firstTriangle, int triangleCount)
    {
        ArgumentNullException.ThrowIfNull(raster);
        if (firstTriangle < 0 || triangleCount is < 1 or > MeshVirtualPageData.MaximumTriangles
            || firstTriangle > raster.Indices.Length / 3 - triangleCount) throw new ArgumentOutOfRangeException(nameof(triangleCount));
        var remap = new Dictionary<uint, uint>(); var source = new List<int>(); var indices = new uint[triangleCount * 3];
        var input = raster.Indices.Slice(firstTriangle * 3, indices.Length);
        for (var i = 0; i < input.Length; i++) {
            if (!remap.TryGetValue(input[i], out var vertex)) {
                vertex = (uint)source.Count; remap.Add(input[i], vertex); source.Add((int)input[i]);
            }
            indices[i] = vertex;
        }
        return new(raster, source.ToArray(), indices);
    }
}
