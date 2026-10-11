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
        Validate();
        ContentHash = ComputeContentHash();
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

    private void Validate()
    {
        if (_vertices.Length == 0 || _indices.Length == 0 || _indices.Length % 3 != 0)
            throw new ArgumentException("Mesh source must contain indexed triangles.");
        if (!Finite(Bounds.Min) || !Finite(Bounds.Max)
            || Bounds.Min.x > Bounds.Max.x || Bounds.Min.y > Bounds.Max.y || Bounds.Min.z > Bounds.Max.z)
            throw new ArgumentException("Mesh bounds must be finite and ordered.");
        foreach (var vertex in _vertices) {
            if (!Finite(vertex.Position) || !Finite(vertex.Normal)
                || !float.IsFinite(vertex.UV.x) || !float.IsFinite(vertex.UV.y) || !vertex.HasFiniteTangent)
                throw new ArgumentException("Mesh attributes must be finite.");
            var p = vertex.Position;
            if (p.x < Bounds.Min.x || p.y < Bounds.Min.y || p.z < Bounds.Min.z
                || p.x > Bounds.Max.x || p.y > Bounds.Max.y || p.z > Bounds.Max.z)
                throw new ArgumentException("Mesh bounds must contain every source vertex.");
        }
        foreach (var index in _indices)
            if (index >= _vertices.Length) throw new ArgumentException("Mesh index is outside the vertex array.");
        var first = 0;
        foreach (var section in _sections) {
            if (section.FirstIndex != first || section.IndexCount <= 0 || section.IndexCount % 3 != 0
                || section.IndexCount > _indices.Length - first || section.MaterialSlot < 0)
                throw new ArgumentException("Mesh sections must partition all triangles into nonnegative material slots.");
            first += section.IndexCount;
        }
        if (first != _indices.Length) throw new ArgumentException("Mesh sections must cover every triangle.");
        if (_bakeUvs.Length != 0 && _bakeUvs.Length != _vertices.Length)
            throw new ArgumentException("A present bake UV channel must have one value per vertex.");
        foreach (var uv in _bakeUvs)
            if (!float.IsFinite(uv.x) || !float.IsFinite(uv.y))
                throw new ArgumentException("Bake UVs must be finite.");
    }

    private static bool Finite(float3 v) => float.IsFinite(v.x) && float.IsFinite(v.y) && float.IsFinite(v.z);

    private string ComputeContentHash()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("SIAMESHSOURCE1"u8);
        Span<byte> bytes = stackalloc byte[48];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, _vertices.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[4..], _indices.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[8..], _sections.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[12..], _bakeUvs.Length);
        hash.AppendData(bytes[..16]);
        Write(bytes, 0, Bounds.Min); Write(bytes, 12, Bounds.Max);
        hash.AppendData(bytes[..24]);
        foreach (var vertex in _vertices) {
            Write(bytes, 0, vertex.Position); Write(bytes, 12, vertex.Normal);
            Write(bytes, 24, vertex.UV.x); Write(bytes, 28, vertex.UV.y);
            Write(bytes, 32, vertex.Tangent.x); Write(bytes, 36, vertex.Tangent.y);
            Write(bytes, 40, vertex.Tangent.z); Write(bytes, 44, vertex.Tangent.w);
            hash.AppendData(bytes);
        }
        foreach (var index in _indices) {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, index);
            hash.AppendData(bytes[..4]);
        }
        foreach (var section in _sections) {
            BinaryPrimitives.WriteInt32LittleEndian(bytes, section.FirstIndex);
            BinaryPrimitives.WriteInt32LittleEndian(bytes[4..], section.IndexCount);
            BinaryPrimitives.WriteInt32LittleEndian(bytes[8..], section.MaterialSlot);
            hash.AppendData(bytes[..12]);
        }
        foreach (var uv in _bakeUvs) {
            Write(bytes, 0, uv.x); Write(bytes, 4, uv.y);
            hash.AppendData(bytes[..8]);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void Write(Span<byte> bytes, int offset, float value)
        => BinaryPrimitives.WriteSingleLittleEndian(bytes[offset..], value);

    private static void Write(Span<byte> bytes, int offset, float3 value)
    {
        Write(bytes, offset, value.x); Write(bytes, offset + 4, value.y); Write(bytes, offset + 8, value.z);
    }
}
