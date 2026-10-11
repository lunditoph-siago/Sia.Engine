using System.Security.Cryptography;
using System.Text;
using Sia.Math;

namespace Sia.Engine.Mesh;

/// <summary>An independently owned exact indexed representation built directly from source.</summary>
public sealed partial class MeshRasterData
{
    private readonly MeshVertex[] _vertices;
    private readonly uint[] _indices;
    private readonly MeshSection[] _sections;
    private readonly float2[] _bakeUvs;

    public ReadOnlySpan<MeshVertex> Vertices => _vertices;
    public ReadOnlySpan<uint> Indices => _indices;
    public ReadOnlySpan<MeshSection> Sections => _sections;
    public ReadOnlySpan<float2> BakeUvs => _bakeUvs;
    public bool HasBakeUvs => _bakeUvs.Length != 0;
    public Aabb Bounds { get; }
    public string SourceHash { get; }
    public string BuildKey { get; }

    private MeshRasterData(MeshSource source)
    {
        _vertices = source.Vertices.ToArray();
        _indices = source.Indices.ToArray();
        _sections = source.Sections.ToArray();
        _bakeUvs = source.BakeUvs.ToArray();
        Bounds = source.Bounds;
        SourceHash = source.ContentHash;
        BuildKey = ComputeBuildKey(SourceHash);
    }

    private MeshRasterData(MeshGeometryCodec.Decoded geometry)
    {
        _vertices = geometry.Geometry.Vertices;
        _indices = geometry.Geometry.Indices;
        _sections = geometry.Sections;
        _bakeUvs = geometry.BakeUvs;
        Bounds = geometry.Geometry.Bounds;
        SourceHash = geometry.Source.ContentHash;
        BuildKey = ComputeBuildKey(SourceHash);
    }

    internal static string ComputeBuildKey(string sourceHash)
        => Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes("SIARASTER1:" + sourceHash)));

    public static MeshRasterData Build(MeshSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new(source);
    }

    public MeshData CopyGeometry() => new([.. _vertices], [.. _indices], Bounds) { BakeUvs = [.. _bakeUvs] };
}
