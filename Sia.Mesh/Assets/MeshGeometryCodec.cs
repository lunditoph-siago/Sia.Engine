using System.Text;
using Sia.Math;

namespace Sia.Engine.Mesh;

// Shared domain encoding/validation; it never opens, owns or caches a stream/asset.
internal static class MeshGeometryCodec
{
    internal readonly record struct Decoded(MeshSource Source, MeshData Geometry, MeshSection[] Sections, float2[] BakeUvs);

    public static void Write(Stream stream, ReadOnlySpan<byte> signature,
        ReadOnlySpan<MeshVertex> vertices, ReadOnlySpan<uint> indices, ReadOnlySpan<MeshSection> sections,
        ReadOnlySpan<float2> bakeUvs, Aabb bounds, string contentHash)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(signature);
        writer.Write(vertices.Length); writer.Write(indices.Length); writer.Write(sections.Length);
        writer.Write((byte)(bakeUvs.IsEmpty ? 0 : 1));
        Vector(writer, bounds.Min); Vector(writer, bounds.Max);
        writer.Write(Convert.FromHexString(contentHash));
        foreach (var vertex in vertices) {
            Vector(writer, vertex.Position); Vector(writer, vertex.Normal);
            writer.Write(vertex.UV.x); writer.Write(vertex.UV.y);
            writer.Write(vertex.Tangent.x); writer.Write(vertex.Tangent.y);
            writer.Write(vertex.Tangent.z); writer.Write(vertex.Tangent.w);
        }
        foreach (var index in indices) writer.Write(index);
        foreach (var section in sections) {
            writer.Write(section.FirstIndex); writer.Write(section.IndexCount); writer.Write(section.MaterialSlot);
        }
        foreach (var uv in bakeUvs) { writer.Write(uv.x); writer.Write(uv.y); }
    }

    public static Decoded Read(Stream stream, ReadOnlySpan<byte> signature, ulong maximumBytes)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Span<byte> bytes = stackalloc byte[32];
        stream.ReadExactly(bytes[..8]);
        if (!bytes[..8].SequenceEqual(signature)) throw new InvalidDataException("Unsupported Mesh geometry signature.");
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        var vertexCount = reader.ReadInt32();
        var indexCount = reader.ReadInt32();
        var sectionCount = reader.ReadInt32();
        var flags = reader.ReadByte();
        if (vertexCount <= 0 || indexCount <= 0 || indexCount % 3 != 0
            || sectionCount <= 0 || sectionCount > indexCount / 3 || flags > 1)
            throw new InvalidDataException("Invalid Mesh geometry counts or flags.");
        var bounds = new Aabb(Vector(reader), Vector(reader));
        stream.ReadExactly(bytes);
        var contentHash = Convert.ToHexString(bytes);
        var bakeBytes = flags == 0 ? 0 : (ulong)vertexCount * 8;
        var encodedBytes = 77ul + (ulong)vertexCount * 48
            + (ulong)indexCount * 4 + (ulong)sectionCount * 12 + bakeBytes;
        var ownedBytes = (ulong)vertexCount * MeshVertex.Stride + (ulong)indexCount * 4
            + (ulong)sectionCount * 12 + (flags == 0 ? 0ul : (ulong)vertexCount * (ulong)System.Runtime.CompilerServices.Unsafe.SizeOf<float2>());
        if (encodedBytes > maximumBytes || ownedBytes > maximumBytes)
            throw new InvalidDataException("Mesh geometry exceeds its encoded/owned payload budget.");
        if (stream.CanSeek && (ulong)(stream.Length - stream.Position) != encodedBytes - 77ul)
            throw new InvalidDataException("Mesh geometry payload length does not match its counts.");
        var vertices = new MeshVertex[vertexCount];
        var indices = new uint[indexCount];
        var sections = new MeshSection[sectionCount];
        var bakeUvs = flags == 0 ? [] : new float2[vertexCount];
        for (var i = 0; i < vertices.Length; i++) {
            var position = Vector(reader); var normal = Vector(reader);
            var uv = new float2(reader.ReadSingle(), reader.ReadSingle());
            var tangent = new float4(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            vertices[i] = new(position, normal, uv) { Tangent = tangent };
        }
        for (var i = 0; i < indices.Length; i++) indices[i] = reader.ReadUInt32();
        for (var i = 0; i < sections.Length; i++) sections[i] = new(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());
        for (var i = 0; i < bakeUvs.Length; i++) bakeUvs[i] = new(reader.ReadSingle(), reader.ReadSingle());
        if (stream.ReadByte() != -1) throw new InvalidDataException("Trailing Mesh geometry data.");
        try {
            var geometry = new MeshData(vertices, indices, bounds) { BakeUvs = bakeUvs };
            var source = MeshSource.FromDecoded(geometry, sections, bakeUvs);
            if (source.ContentHash != contentHash) throw new InvalidDataException("Mesh geometry content hash mismatch.");
            return new(source, geometry, sections, bakeUvs);
        }
        catch (ArgumentException exception) {
            throw new InvalidDataException("Invalid Mesh geometry payload.", exception);
        }
    }

    private static void Vector(BinaryWriter writer, float3 value)
    { writer.Write(value.x); writer.Write(value.y); writer.Write(value.z); }
    private static float3 Vector(BinaryReader reader) => new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
}
