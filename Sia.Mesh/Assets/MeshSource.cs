using System.Buffers.Binary;
using System.Security.Cryptography;
using Sia.Math;

namespace Sia.Engine.Mesh;

/// <summary>Owned, read-only source geometry without scene bindings or GPU resources.</summary>
public sealed partial class MeshSource
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
    public string ContentHash { get; }

    private MeshSource(MeshData data, MeshSection[] sections, float2[] bakeUvs)
    {
        _vertices = data.Vertices;
        _indices = data.Indices;
        _sections = sections;
        _bakeUvs = bakeUvs;
        Bounds = data.Bounds;
        ValidateGeometry(_vertices, _indices, _sections, _bakeUvs, Bounds);
        ContentHash = ComputeContentHash(_vertices, _indices, _sections, _bakeUvs, Bounds);
    }

    public static MeshSource Create(MeshData data, ReadOnlySpan<MeshSection> sections = default,
        ReadOnlySpan<float2> bakeUvs = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(data.Vertices);
        ArgumentNullException.ThrowIfNull(data.Indices);
        data.ValidateBakeUvs();
        return new(new([.. data.Vertices], [.. data.Indices], data.Bounds),
            sections.IsEmpty ? [new(0, data.Indices.Length, 0)] : sections.ToArray(),
            bakeUvs.IsEmpty ? data.BakeUvs.ToArray() : bakeUvs.ToArray());
    }

    // Only the domain decoder transfers its freshly allocated arrays; public construction snapshots input.
    internal static MeshSource FromDecoded(MeshData data, MeshSection[] sections, float2[] bakeUvs)
        => new(data, sections, bakeUvs);

    public MeshData CopyGeometry() => new([.. _vertices], [.. _indices], Bounds) { BakeUvs = [.. _bakeUvs] };

    internal static void ValidateGeometry(ReadOnlySpan<MeshVertex> vertices, ReadOnlySpan<uint> indices,
        ReadOnlySpan<MeshSection> sections, ReadOnlySpan<float2> bakeUvs, Aabb bounds)
    {
        if (vertices.Length == 0 || indices.Length == 0 || indices.Length % 3 != 0)
            throw new ArgumentException("Mesh source must contain indexed triangles.");
        if (!Finite(bounds.Min) || !Finite(bounds.Max)
            || bounds.Min.x > bounds.Max.x || bounds.Min.y > bounds.Max.y || bounds.Min.z > bounds.Max.z)
            throw new ArgumentException("Mesh bounds must be finite and ordered.");
        foreach (var vertex in vertices) {
            if (!Finite(vertex.Position) || !Finite(vertex.Normal)
                || !float.IsFinite(vertex.UV.x) || !float.IsFinite(vertex.UV.y) || !vertex.HasFiniteTangent)
                throw new ArgumentException("Mesh attributes must be finite.");
            var p = vertex.Position;
            if (p.x < bounds.Min.x || p.y < bounds.Min.y || p.z < bounds.Min.z
                || p.x > bounds.Max.x || p.y > bounds.Max.y || p.z > bounds.Max.z)
                throw new ArgumentException("Mesh bounds must contain every source vertex.");
        }
        foreach (var index in indices)
            if (index >= vertices.Length) throw new ArgumentException("Mesh index is outside the vertex array.");
        var first = 0;
        foreach (var section in sections) {
            if (section.FirstIndex != first || section.IndexCount <= 0 || section.IndexCount % 3 != 0
                || section.IndexCount > indices.Length - first || section.MaterialSlot < 0)
                throw new ArgumentException("Mesh sections must partition all triangles into nonnegative material slots.");
            first += section.IndexCount;
        }
        if (first != indices.Length) throw new ArgumentException("Mesh sections must cover every triangle.");
        if (bakeUvs.Length != 0 && bakeUvs.Length != vertices.Length)
            throw new ArgumentException("A present bake UV channel must have one value per vertex.");
        foreach (var uv in bakeUvs)
            if (!float.IsFinite(uv.x) || !float.IsFinite(uv.y))
                throw new ArgumentException("Bake UVs must be finite.");
    }

    private static bool Finite(float3 v) => float.IsFinite(v.x) && float.IsFinite(v.y) && float.IsFinite(v.z);

    internal static string ComputeContentHash(ReadOnlySpan<MeshVertex> vertices, ReadOnlySpan<uint> indices,
        ReadOnlySpan<MeshSection> sections, ReadOnlySpan<float2> bakeUvs, Aabb bounds, CancellationToken cancellationToken = default)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("SIAMESHSOURCE"u8);
        Span<byte> bytes = stackalloc byte[48];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, vertices.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[4..], indices.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[8..], sections.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[12..], bakeUvs.Length);
        hash.AppendData(bytes[..16]);
        Write(bytes, 0, bounds.Min); Write(bytes, 12, bounds.Max);
        hash.AppendData(bytes[..24]);
        Span<byte> buffer = stackalloc byte[32 * 1024];
        var writer = new MeshDataIO.Writer(Stream.Null, hash, buffer, cancellationToken);
        foreach (var vertex in vertices) writer.Vertex(vertex);
        foreach (var index in indices) writer.UInt(index);
        foreach (var section in sections) { writer.Int(section.FirstIndex); writer.Int(section.IndexCount); writer.Int(section.MaterialSlot); }
        foreach (var uv in bakeUvs) { writer.Float(uv.x); writer.Float(uv.y); }
        writer.Flush(); cancellationToken.ThrowIfCancellationRequested();
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void Write(Span<byte> bytes, int offset, float value)
        => BinaryPrimitives.WriteSingleLittleEndian(bytes[offset..], value);

    private static void Write(Span<byte> bytes, int offset, float3 value)
    {
        Write(bytes, offset, value.x); Write(bytes, offset + 4, value.y); Write(bytes, offset + 8, value.z);
    }
}
