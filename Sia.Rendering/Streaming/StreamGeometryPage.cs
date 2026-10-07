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
    public bool HasLightmapUV { get; }
    public int EncodedVertexBytes => HasLightmapUV ? 56 : 48;

    public ReadOnlySpan<float4> Vertices => MemoryMarshal.Cast<byte, float4>(Bytes.Span.Slice(16, VertexCount * 48));

    public ReadOnlySpan<uint> Indices => MemoryMarshal.Cast<byte, uint>(Bytes.Span.Slice(16 + VertexCount * 48, TriangleCount * 12));

    /// <summary>Independent bake coordinates retained as a page sidecar; absent on legacy pages.</summary>
    public ReadOnlySpan<float> LightmapCoordinates => HasLightmapUV
        ? MemoryMarshal.Cast<byte, float>(Bytes.Span[(16 + VertexCount * 48 + TriangleCount * 12)..]) : [];

    public float2 GetLightmapUV(int vertex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(vertex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(vertex, VertexCount);
        if (!HasLightmapUV) throw new InvalidOperationException("The page has no lightmap coordinates.");
        var coordinates = LightmapCoordinates;
        return new(coordinates[vertex * 2], coordinates[vertex * 2 + 1]);
    }

    private StreamGeometryPage(ReadOnlyMemory<byte> bytes, int vertices, int triangles, bool lightmap)
        => (Bytes, VertexCount, TriangleCount, HasLightmapUV) = (bytes, vertices, triangles, lightmap);

    public static StreamGeometryPage Decode(ReadOnlyMemory<byte> bytes)
    {
        var lightmap = bytes.Span.StartsWith("SIAPAGE1"u8);
        if (!BitConverter.IsLittleEndian || bytes.Length is < 28 or > MaximumBytes
            || (!lightmap && !bytes.Span[..8].SequenceEqual("SIAPAGE\0"u8)))
            throw new InvalidDataException("Invalid streaming geometry page.");
        var vertices = BinaryPrimitives.ReadInt32LittleEndian(bytes.Span[8..]);
        var triangles = BinaryPrimitives.ReadInt32LittleEndian(bytes.Span[12..]);
        if (vertices <= 0 || triangles <= 0 || 16L + (vertices * (lightmap ? 56L : 48L)) + (triangles * 12L) != bytes.Length)
            throw new InvalidDataException("Streaming page reservation disagrees with its payload.");
        var page = new StreamGeometryPage(bytes, vertices, triangles, lightmap);
        foreach (var v in page.Vertices)
            if (!float.IsFinite(v.x) || !float.IsFinite(v.y) || !float.IsFinite(v.z) || !float.IsFinite(v.w))
                throw new InvalidDataException("Nonfinite streaming vertex.");
        foreach (var index in page.Indices)
            if (index >= vertices) throw new InvalidDataException("Streaming triangle index is out of range.");
        foreach (var uv in page.LightmapCoordinates)
            if (!float.IsFinite(uv))
                throw new InvalidDataException("Nonfinite streaming lightmap coordinate.");
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
        var lightmap = unique.Any(v => v.LightmapUV.x != 0 || v.LightmapUV.y != 0);
        var length = 16L + (unique.Count * (lightmap ? 56L : 48L)) + (mapped.Length * 4L);
        if (length > MaximumBytes) throw new ArgumentException("Geometry page exceeds its cooked byte limit.");
        var bytes = new byte[(int)length];
        (lightmap ? "SIAPAGE1"u8 : "SIAPAGE\0"u8).CopyTo(bytes);
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
        if (lightmap) {
            var coordinates = MemoryMarshal.Cast<byte, float>(bytes.AsSpan(16 + unique.Count * 48 + mapped.Length * 4));
            for (var i = 0; i < unique.Count; i++) {
                coordinates[i * 2] = unique[i].LightmapUV.x;
                coordinates[i * 2 + 1] = unique[i].LightmapUV.y;
            }
        }
        return Decode(bytes);
    }
}
