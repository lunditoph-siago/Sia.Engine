using Sia.Math;

namespace Sia.Engine.Mesh;

/// <summary>One independently loadable mesh-local triangle page, with every source vertex channel.</summary>
public sealed partial class MeshVirtualPageData
{
    public const int MaximumTriangles = 1024;
    public const int MaximumEncodedBytes = 256 * 1024;
    private readonly MeshData _geometry;
    public ReadOnlySpan<MeshVertex> Vertices => _geometry.Vertices;
    public ReadOnlySpan<uint> Indices => _geometry.Indices;
    public ReadOnlySpan<float2> BakeUvs => _geometry.BakeUvs;
    public bool HasBakeUvs => _geometry.HasBakeUvs;
    public Aabb Bounds => _geometry.Bounds;
    public int TriangleCount => _geometry.Indices.Length / 3;
    public ulong PayloadBytes => (ulong)Vertices.Length * (ulong)(MeshVertex.Stride + (HasBakeUvs ? 8 : 0))
        + (ulong)Indices.Length * 4;
    public int EncodedBytes => checked(73 + Vertices.Length * (HasBakeUvs ? 56 : 48) + Indices.Length * 4);
    public string ContentHash { get; }

    private MeshVirtualPageData(MeshData geometry, string? contentHash = null, CancellationToken cancellationToken = default)
    {
        Validate(geometry);
        _geometry = geometry;
        ContentHash = contentHash ?? Write(Stream.Null, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>Copies/remaps only the selected triangles; source storage and lifetime stay with the caller.</summary>
    public static MeshVirtualPageData Create(ReadOnlySpan<MeshVertex> vertices, ReadOnlySpan<uint> indices,
        ReadOnlySpan<float2> bakeUvs = default, ulong maximumBytes = MaximumEncodedBytes,
        CancellationToken cancellationToken = default)
    {
        if (indices.Length is <= 0 or > MaximumTriangles * 3 || indices.Length % 3 != 0)
            throw new ArgumentException("A virtual page requires 1..1024 oriented triangles.", nameof(indices));
        var compact = MeshPatchBuilder.Compact(vertices, indices, bakeUvs, maximumBytes, cancellationToken);
        var result = new MeshVirtualPageData(compact, cancellationToken: cancellationToken);
        if ((ulong)result.EncodedBytes > maximumBytes || result.PayloadBytes > maximumBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes), "Virtual page exceeds its encoded/owned-array budget.");
        return result;
    }

    private static void Validate(MeshData geometry)
    {
        if (geometry.Vertices.Length is <= 0 or > MaximumTriangles * 3
            || geometry.Indices.Length is <= 0 or > MaximumTriangles * 3 || geometry.Indices.Length % 3 != 0
            || !math.all(math.isfinite(geometry.Bounds.Min)) || !math.all(math.isfinite(geometry.Bounds.Max))
            || math.any(geometry.Bounds.Min > geometry.Bounds.Max))
            throw new InvalidDataException("Invalid virtual page shape or bounds.");
        geometry.ValidateBakeUvs();
        foreach (var vertex in geometry.Vertices)
            if (!math.all(math.isfinite(vertex.Position)) || !math.all(math.isfinite(vertex.Normal))
                || !math.all(math.isfinite(vertex.UV)) || !vertex.HasFiniteTangent
                || math.any(vertex.Position < geometry.Bounds.Min) || math.any(vertex.Position > geometry.Bounds.Max))
                throw new InvalidDataException("Invalid virtual page vertex or bounds coverage.");
        var referenced = new bool[geometry.Vertices.Length];
        foreach (var index in geometry.Indices) {
            if (index >= geometry.Vertices.Length) throw new InvalidDataException("Invalid virtual page index.");
            referenced[index] = true;
        }
        if (referenced.Contains(false)) throw new InvalidDataException("Virtual pages cannot retain unused vertices.");
    }
}
