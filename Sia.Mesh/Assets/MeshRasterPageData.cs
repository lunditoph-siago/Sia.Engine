using System.Runtime.CompilerServices;
using Sia.Math;

namespace Sia.Engine.Mesh;

public enum MeshRasterPageKind { Vertices, Indices }

/// <summary>One exact Raster vertex-channel or global-index page; the two payload kinds never overlap.</summary>
public sealed partial class MeshRasterPageData
{
    public const int MaximumVertices = 4096;
    public const int MaximumTriangles = 1024;
    public const int MaximumEncodedBytes = 256 * 1024;
    public const int DefaultMaximumBytes = 512 * 1024;
    private readonly MeshVertex[] _vertices;
    private readonly float2[] _bakeUvs;
    private readonly uint[] _indices;
    public MeshRasterPageKind Kind { get; }
    public ReadOnlySpan<MeshVertex> Vertices => _vertices;
    public ReadOnlySpan<float2> BakeUvs => _bakeUvs;
    public ReadOnlySpan<uint> Indices => _indices;
    public bool HasBakeUvs => _bakeUvs.Length != 0;
    public int ElementCount => Kind == MeshRasterPageKind.Vertices ? _vertices.Length : _indices.Length;
    public int EncodedBytes => checked(64 + ElementCount * (Kind == MeshRasterPageKind.Indices ? 4 : HasBakeUvs ? 56 : 48));
    public ulong PayloadBytes => OwnedBytes(_vertices.Length, _indices.Length, HasBakeUvs);
    public string ContentHash { get; }

    private MeshRasterPageData(MeshRasterPageKind kind, MeshVertex[] vertices, float2[] bakeUvs, uint[] indices,
        string? contentHash = null, CancellationToken cancellationToken = default)
    {
        Kind = kind; _vertices = vertices; _bakeUvs = bakeUvs; _indices = indices;
        Validate(cancellationToken); ContentHash = contentHash ?? Write(Stream.Null, cancellationToken);
    }

    public static MeshRasterPageData CreateVertices(ReadOnlySpan<MeshVertex> vertices, ReadOnlySpan<float2> bakeUvs = default,
        ulong maximumBytes = DefaultMaximumBytes, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (vertices.Length is <= 0 or > MaximumVertices || !bakeUvs.IsEmpty && bakeUvs.Length != vertices.Length)
            throw new ArgumentException("Vertex pages require 1..4096 vertices and an absent or complete bake channel.");
        RequireBudget(vertices.Length, 0, !bakeUvs.IsEmpty, maximumBytes);
        return new(MeshRasterPageKind.Vertices, vertices.ToArray(), bakeUvs.ToArray(), [], cancellationToken: cancellationToken);
    }

    public static MeshRasterPageData CreateIndices(ReadOnlySpan<uint> indices, ulong maximumBytes = DefaultMaximumBytes,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (indices.Length is <= 0 or > MaximumTriangles * 3 || indices.Length % 3 != 0)
            throw new ArgumentException("Index pages require 1..1024 oriented triangles.");
        RequireBudget(0, indices.Length, false, maximumBytes);
        return new(MeshRasterPageKind.Indices, [], [], indices.ToArray(), cancellationToken: cancellationToken);
    }

    private void Validate(CancellationToken token)
    {
        if (Kind == MeshRasterPageKind.Vertices) {
            if (_vertices.Length is <= 0 or > MaximumVertices || _indices.Length != 0
                || _bakeUvs.Length != 0 && _bakeUvs.Length != _vertices.Length)
                throw new InvalidDataException("Invalid Raster vertex-page shape.");
            for (var index = 0; index < _vertices.Length; index++) {
                if ((index & 255) == 0) token.ThrowIfCancellationRequested();
                var vertex = _vertices[index];
                if (!math.all(math.isfinite(vertex.Position)) || !math.all(math.isfinite(vertex.Normal))
                    || !math.all(math.isfinite(vertex.UV)) || !vertex.HasFiniteTangent
                    || HasBakeUvs && !math.all(math.isfinite(_bakeUvs[index])))
                    throw new InvalidDataException("Raster vertex pages require finite original channels.");
            }
        }
        else if (Kind != MeshRasterPageKind.Indices || _vertices.Length != 0 || _bakeUvs.Length != 0
            || _indices.Length is <= 0 or > MaximumTriangles * 3 || _indices.Length % 3 != 0)
            throw new InvalidDataException("Invalid Raster global-index-page shape.");
    }

    private static ulong OwnedBytes(int vertices, int indices, bool bake)
        => (ulong)vertices * ((ulong)Unsafe.SizeOf<MeshVertex>() + (bake ? (ulong)Unsafe.SizeOf<float2>() : 0)) + (ulong)indices * 4;

    private static void RequireBudget(int vertices, int indices, bool bake, ulong maximumBytes)
    {
        var encoded = 64ul + (ulong)vertices * (bake ? 56ul : 48ul) + (ulong)indices * 4;
        if (encoded > MaximumEncodedBytes || encoded > maximumBytes || OwnedBytes(vertices, indices, bake) > maximumBytes)
            throw new InvalidDataException("Raster page exceeds its encoded or known-array budget.");
    }
}
