using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Sia.Asset;
using Sia.Math;

namespace Sia.Engine.Rendering;

public sealed partial class SceneTraceData
{
    /// <summary>Writes the exact immutable tracing data; leaves the destination open.</summary>
    public void Write(Stream destination) => ChecksummedAsset.Write<SceneTraceData, Codec>(destination, this);

    /// <summary>Reads bounded, checksummed tracing data and validates its complete topology before use.</summary>
    public static SceneTraceData Read(Stream source, ulong maximumBytes = 128ul * 1024 * 1024)
        => ChecksummedAsset.Read<SceneTraceData, Codec>(source, PayloadLimit(maximumBytes));

    /// <summary>Decodes an existing encoded buffer without an additional complete payload copy.</summary>
    public static SceneTraceData Decode(ReadOnlyMemory<byte> source, ulong maximumBytes = 128ul * 1024 * 1024)
        => ChecksummedAsset.Read<SceneTraceData, Codec>(source, PayloadLimit(maximumBytes));

    private static int PayloadLimit(ulong maximumBytes)
        => checked(Codec.HeaderBytes + (int)System.Math.Min(maximumBytes, MaximumPackedBytes(4_000_000)));

    private readonly struct Codec : IChecksummedAssetCodec<SceneTraceData>
    {
        public static int HeaderBytes => 52;
        public static int GetWriteSize(SceneTraceData value) => checked(HeaderBytes + value.Packed.Length * 16);

        public static int GetReadSize(ReadOnlySpan<byte> header)
        {
            if (!header[..8].SequenceEqual("SIATRACE"u8) || BinaryPrimitives.ReadInt32LittleEndian(header[8..]) != 1)
                throw new InvalidDataException("Unsupported scene trace asset.");
            var triangles = BinaryPrimitives.ReadInt32LittleEndian(header[12..]);
            var records = BinaryPrimitives.ReadInt32LittleEndian(header[16..]);
            if (triangles is < 1 or > 4_000_000)
                throw new InvalidDataException("Scene trace triangle count exceeds limits.");
            var minimum = checked(2 + NodeCount(triangles) * 3 + triangles + 5);
            if (records < minimum || (ulong)records * 16 > MaximumPackedBytes(triangles))
                throw new InvalidDataException("Scene trace record count exceeds limits.");
            return checked(HeaderBytes + records * 16);
        }

        public static void WritePayload(BinaryWriter writer, SceneTraceData value)
        {
            writer.Write("SIATRACE"u8);
            writer.Write(1);
            writer.Write(value.TriangleCount);
            writer.Write(value.Packed.Length);
            writer.Write(value.Identity.Span);
            if (BitConverter.IsLittleEndian) writer.Write(MemoryMarshal.AsBytes(value.Packed.Span));
            else foreach (var v in value.Packed.Span) {
                writer.Write(v.x); writer.Write(v.y); writer.Write(v.z); writer.Write(v.w);
            }
        }

        public static SceneTraceData ReadPayload(BinaryReader reader)
        {
            reader.ReadBytes(12); // Magic/version and bounded counts were admitted before body allocation.
            var triangles = reader.ReadInt32();
            var records = reader.ReadInt32();
            var identity = reader.ReadBytes(32);
            var packed = GC.AllocateUninitializedArray<float4>(records);
            if (BitConverter.IsLittleEndian) reader.BaseStream.ReadExactly(MemoryMarshal.AsBytes(packed.AsSpan()));
            else for (var i = 0; i < packed.Length; i++)
                packed[i] = new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            ValidatePacked(packed, triangles);
            return new((packed, triangles), identity);
        }
    }

    private static void ValidatePacked(ReadOnlySpan<float4> packed, int count)
    {
        var nodeCount = NodeCount(count);
        var triangleOffset = checked(2 + nodeCount * 3);
        var vertexOffset = checked(triangleOffset + count);
        var surfaceOffset = TraceIndex(packed[0].w, packed.Length);
        if (!math.all(packed[0].xyz == new float3(nodeCount, triangleOffset, count))
            || !math.all(packed[1] == new float4(vertexOffset, 0, 0, 0))
            || surfaceOffset < vertexOffset + 3 || surfaceOffset > packed.Length - 2
            || (packed.Length - surfaceOffset) % 2 != 0)
            throw new InvalidDataException("Invalid scene trace section layout.");
        var vertices = packed[vertexOffset..surfaceOffset];
        foreach (var vertex in vertices)
            if (!math.all(math.isfinite(vertex)) || vertex.w != 0)
                throw new InvalidDataException("Invalid scene trace vertex.");
        for (var i = surfaceOffset; i < packed.Length; i += 2) {
            var albedo = packed[i]; var emission = packed[i + 1];
            if (!math.all(math.isfinite(albedo)) || !math.all(math.isfinite(emission))
                || math.any(albedo.xyz < 0) || math.any(emission.xyz < 0)
                || albedo.w is not (0 or 1) || emission.w != 0)
                throw new InvalidDataException("Invalid scene trace surface.");
        }
        var nodes = packed.Slice(2, nodeCount * 3);
        var triangles = packed.Slice(triangleOffset, count);
        if (ValidateNode(nodes, triangles, vertices, (packed.Length - surfaceOffset) / 2, 0, 0, count) != nodeCount)
            throw new InvalidDataException("Scene trace hierarchy is incomplete.");
    }

    private static int ValidateNode(ReadOnlySpan<float4> nodes, ReadOnlySpan<float4> triangles,
        ReadOnlySpan<float4> vertices, int surfaces, int node, int start, int count)
    {
        if ((uint)node >= nodes.Length / 3)
            throw new InvalidDataException("Scene trace hierarchy exceeds its section.");
        var minimum = nodes[node * 3]; var maximum = nodes[node * 3 + 1];
        var leaf = nodes[node * 3 + 2];
        if (!math.all(math.isfinite(minimum)) || !math.all(math.isfinite(maximum))
            || math.any(minimum.xyz > maximum.xyz) || maximum.w != start
            || !math.all(leaf == new float4(count <= 8 ? count : 0, 0, 0, 0)))
            throw new InvalidDataException("Invalid scene trace node.");
        var next = node + 1;
        float3 lo, hi;
        if (count <= 8) {
            lo = new(float.PositiveInfinity); hi = new(float.NegativeInfinity);
            for (var i = start; i < start + count; i++) {
                var t = triangles[i];
                var a = vertices[TraceIndex(t.x, vertices.Length)].xyz;
                var b = vertices[TraceIndex(t.y, vertices.Length)].xyz;
                var c = vertices[TraceIndex(t.z, vertices.Length)].xyz;
                TraceIndex(t.w, surfaces);
                var area2 = math.lengthsq(math.cross(b - a, c - a));
                if (!float.IsFinite(area2) || area2 < 1e-16f)
                    throw new InvalidDataException("Invalid scene trace triangle.");
                lo = math.min(lo, math.min(a, math.min(b, c)));
                hi = math.max(hi, math.max(a, math.max(b, c)));
            }
        }
        else {
            var leftCount = count / 2;
            var right = ValidateNode(nodes, triangles, vertices, surfaces, next, start, leftCount);
            next = ValidateNode(nodes, triangles, vertices, surfaces, right, start + leftCount, count - leftCount);
            lo = math.min(nodes[(node + 1) * 3].xyz, nodes[right * 3].xyz);
            hi = math.max(nodes[(node + 1) * 3 + 1].xyz, nodes[right * 3 + 1].xyz);
        }
        if (minimum.w != next || !math.all(minimum.xyz == lo) || !math.all(maximum.xyz == hi))
            throw new InvalidDataException("Scene trace escape index or bounds do not match its geometry.");
        return next;
    }

    private static int TraceIndex(float value, int count)
    {
        if (!float.IsFinite(value) || value < 0 || value >= count)
            throw new InvalidDataException("Invalid scene trace index.");
        var index = (int)value;
        if (index != value)
            throw new InvalidDataException("Invalid scene trace index.");
        return index;
    }
}
