using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Sia.Engine.Mesh;
using Sia.Math;

namespace Sia.Engine.Rendering;

/// <summary>Independently validated GPU-ready geometry with direct triangle indices.</summary>
public sealed class StreamGeometryPage
{
    public const int MaximumBytes = 128 * 1024;

    public ReadOnlyMemory<byte> Bytes { get; }
    public int VertexCount { get; }
    public int TriangleCount { get; }

    public ReadOnlySpan<float4> Vertices => MemoryMarshal.Cast<byte, float4>(Bytes.Span.Slice(16, VertexCount * 48));

    public ReadOnlySpan<uint> Indices => MemoryMarshal.Cast<byte, uint>(Bytes.Span[(16 + (VertexCount * 48))..]);

    private StreamGeometryPage(ReadOnlyMemory<byte> bytes, int vertices, int triangles)
        => (Bytes, VertexCount, TriangleCount) = (bytes, vertices, triangles);

    public static StreamGeometryPage Decode(ReadOnlyMemory<byte> bytes)
    {
        if (!BitConverter.IsLittleEndian || bytes.Length is < 28 or > MaximumBytes || !bytes.Span[..8].SequenceEqual("SIAPAGE\0"u8))
            throw new InvalidDataException("Invalid streaming geometry page.");
        var vertices = BinaryPrimitives.ReadInt32LittleEndian(bytes.Span[8..]);
        var triangles = BinaryPrimitives.ReadInt32LittleEndian(bytes.Span[12..]);
        if (vertices <= 0 || triangles <= 0 || 16L + (vertices * 48L) + (triangles * 12L) != bytes.Length)
            throw new InvalidDataException("Streaming page reservation disagrees with its payload.");
        var page = new StreamGeometryPage(bytes, vertices, triangles);
        foreach (var v in page.Vertices)
            if (!float.IsFinite(v.x) || !float.IsFinite(v.y) || !float.IsFinite(v.z) || !float.IsFinite(v.w))
                throw new InvalidDataException("Nonfinite streaming vertex.");
        foreach (var index in page.Indices)
            if (index >= vertices) throw new InvalidDataException("Streaming triangle index is out of range.");
        return page;
    }

    public static StreamGeometryPage Cook(ReadOnlySpan<MeshVertex> vertices, ReadOnlySpan<uint> indices)
    {
        if (indices.Length == 0 || indices.Length % 3 != 0 || indices.Length > (MaximumBytes - 16) / 4)
            throw new ArgumentException("Streaming pages contain complete nonempty triangles.");
        var remap = new Dictionary<uint, uint>();
        var attributes = new Dictionary<MeshVertex, uint>();
        var unique = new List<MeshVertex>();
        var mapped = new uint[indices.Length];
        for (var i = 0; i < indices.Length; i++) {
            var index = indices[i];
            if (index >= vertices.Length) throw new ArgumentException("Invalid source topology.");
            if (!remap.TryGetValue(index, out var value)) {
                var vertex = vertices[(int)index];
                if (!attributes.TryGetValue(vertex, out value)) {
                    value = (uint)unique.Count;
                    unique.Add(vertex);
                    attributes.Add(vertex, value);
                }
                remap.Add(index, value);
            }
            mapped[i] = value;
        }
        var length = 16L + (unique.Count * 48L) + (mapped.Length * 4L);
        if (length > MaximumBytes) throw new ArgumentException("Geometry page exceeds its cooked byte limit.");
        var bytes = new byte[(int)length];
        "SIAPAGE\0"u8.CopyTo(bytes);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), unique.Count);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12), indices.Length / 3);
        var packed = MemoryMarshal.Cast<byte, float4>(bytes.AsSpan(16, unique.Count * 48));
        for (var i = 0; i < unique.Count; i++) {
            var v = unique[i];
            packed[i] = new(v.Position, v.Normal.x);
            packed[unique.Count + i] = new(v.Normal.y, v.Normal.z, v.UV.x, v.UV.y);
            packed[(unique.Count * 2) + i] = v.Tangent;
        }
        MemoryMarshal.AsBytes(mapped.AsSpan()).CopyTo(bytes.AsSpan(16 + (unique.Count * 48)));
        return Decode(bytes);
    }
}
