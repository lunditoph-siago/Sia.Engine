using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Sia.Math;

namespace Sia.Engine.Mesh;

public sealed partial class MeshBvhData
{
    public void Save(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write("SIAMSHB\0"u8);
        writer.Write(_positions.Length); writer.Write(_triangles.Length); writer.Write(_nodes.Length);
        writer.Write(Settings.MaxLeafTriangles);
        writer.Write(Convert.FromHexString(GeometryHash));
        writer.Write(Convert.FromHexString(BuildKey));
        writer.Write(HashPayload(_positions, _triangles, _nodes));
        foreach (var position in _positions) Vector(writer, position);
        foreach (var triangle in _triangles) {
            writer.Write(triangle.A); writer.Write(triangle.B); writer.Write(triangle.C);
            writer.Write(triangle.SourceTriangle); writer.Write(triangle.MaterialSlot);
        }
        foreach (var node in _nodes) {
            Vector(writer, node.Bounds.Min); Vector(writer, node.Bounds.Max);
            writer.Write(node.EscapeIndex); writer.Write(node.FirstTriangle); writer.Write(node.TriangleCount);
        }
    }

    public static MeshBvhData Load(Stream stream, ulong maximumBytes = 128ul * 1024 * 1024)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Span<byte> bytes = stackalloc byte[32];
        stream.ReadExactly(bytes[..8]);
        if (!bytes[..8].SequenceEqual("SIAMSHB\0"u8)) throw new InvalidDataException("Unsupported Mesh BVH signature.");
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        var vertexCount = reader.ReadInt32();
        var triangleCount = reader.ReadInt32();
        var nodeCount = reader.ReadInt32();
        var settings = new MeshBvhBuildSettings(reader.ReadInt32());
        if (vertexCount <= 0 || triangleCount <= 0 || nodeCount <= 0
            || (ulong)nodeCount > (ulong)triangleCount * 2 - 1 || settings.MaxLeafTriangles is < 1 or > 64)
            throw new InvalidDataException("Invalid Mesh BVH counts or settings.");
        var encodedBytes = 120ul + (ulong)vertexCount * 12 + (ulong)triangleCount * 20 + (ulong)nodeCount * 36;
        var ownedAndValidationBytes = GeometryBytes(vertexCount, triangleCount)
            + (ulong)nodeCount * (ulong)Unsafe.SizeOf<MeshBvhNode>() + (ulong)triangleCount * sizeof(int);
        if (encodedBytes > maximumBytes || ownedAndValidationBytes > maximumBytes)
            throw new InvalidDataException("Mesh BVH exceeds its encoded/decoded validation budget.");
        stream.ReadExactly(bytes); var geometryHash = Convert.ToHexString(bytes);
        stream.ReadExactly(bytes); var buildKey = Convert.ToHexString(bytes);
        stream.ReadExactly(bytes); var expectedPayloadHash = bytes.ToArray();
        if (buildKey != ComputeBuildKey(geometryHash, settings)) throw new InvalidDataException("Mesh BVH build key mismatch.");
        if (stream.CanSeek && (ulong)(stream.Length - stream.Position) != encodedBytes - 120)
            throw new InvalidDataException("Mesh BVH payload length does not match its counts.");
        var positions = new float3[vertexCount];
        var triangles = new MeshBvhTriangle[triangleCount];
        var nodes = new MeshBvhNode[nodeCount];
        for (var i = 0; i < positions.Length; i++) positions[i] = Vector(reader);
        for (var i = 0; i < triangles.Length; i++)
            triangles[i] = new(reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadInt32(), reader.ReadInt32());
        for (var i = 0; i < nodes.Length; i++)
            nodes[i] = new(new(Vector(reader), Vector(reader)), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());
        if (stream.ReadByte() != -1) throw new InvalidDataException("Trailing Mesh BVH data.");
        if (!expectedPayloadHash.AsSpan().SequenceEqual(HashPayload(positions, triangles, nodes)))
            throw new InvalidDataException("Mesh BVH payload hash mismatch.");
        var order = ValidateDecoded(positions, triangles, nodes, settings);
        if (HashDecodedGeometry(positions, triangles, order) != geometryHash)
            throw new InvalidDataException("Mesh BVH geometry hash mismatch.");
        return new(positions, triangles, nodes, settings, geometryHash);
    }

    private static int[] ValidateDecoded(float3[] positions, MeshBvhTriangle[] triangles,
        MeshBvhNode[] nodes, MeshBvhBuildSettings settings)
    {
        foreach (var position in positions)
            if (!math.all(math.isfinite(position))) throw new InvalidDataException("Nonfinite Mesh BVH position.");
        var order = new int[triangles.Length];
        Array.Fill(order, -1);
        for (var i = 0; i < triangles.Length; i++) {
            var t = triangles[i];
            if (t.A >= positions.Length || t.B >= positions.Length || t.C >= positions.Length || t.MaterialSlot < 0
                || t.SourceTriangle < 0 || t.SourceTriangle >= triangles.Length || order[t.SourceTriangle] != -1)
                throw new InvalidDataException("Invalid Mesh BVH primitive identity or vertex reference.");
            order[t.SourceTriangle] = i;
        }
        var nextTriangle = 0;
        for (var i = 0; i < nodes.Length; i++) {
            var node = nodes[i];
            if (!math.all(math.isfinite(node.Bounds.Min)) || !math.all(math.isfinite(node.Bounds.Max))
                || math.any(node.Bounds.Min > node.Bounds.Max) || node.EscapeIndex <= i || node.EscapeIndex > nodes.Length
                || node.TriangleCount < 0 || node.TriangleCount > settings.MaxLeafTriangles)
                throw new InvalidDataException("Invalid Mesh BVH node.");
            if (node.TriangleCount == 0) {
                if (node.FirstTriangle != 0) throw new InvalidDataException("Internal Mesh BVH node contains a triangle offset.");
                continue;
            }
            if (node.EscapeIndex != i + 1 || node.FirstTriangle != nextTriangle
                || node.TriangleCount > triangles.Length - nextTriangle)
                throw new InvalidDataException("Invalid Mesh BVH leaf coverage.");
            for (var t = nextTriangle; t < nextTriangle + node.TriangleCount; t++) {
                var triangle = triangles[t];
                if (!Contains(node.Bounds, positions[triangle.A]) || !Contains(node.Bounds, positions[triangle.B])
                    || !Contains(node.Bounds, positions[triangle.C]))
                    throw new InvalidDataException("Mesh BVH leaf does not contain its triangles.");
            }
            nextTriangle += node.TriangleCount;
        }
        if (nodes[0].EscapeIndex != nodes.Length || nextTriangle != triangles.Length)
            throw new InvalidDataException("Incomplete Mesh BVH tree coverage.");
        for (var i = 0; i < nodes.Length; i++) {
            var node = nodes[i];
            if (node.TriangleCount != 0) continue;
            var left = i + 1;
            if (left >= node.EscapeIndex) throw new InvalidDataException("Mesh BVH node has no children.");
            var right = nodes[left].EscapeIndex;
            if (right <= left || right >= node.EscapeIndex || nodes[right].EscapeIndex != node.EscapeIndex
                || !Contains(node.Bounds, nodes[left].Bounds.Min) || !Contains(node.Bounds, nodes[left].Bounds.Max)
                || !Contains(node.Bounds, nodes[right].Bounds.Min) || !Contains(node.Bounds, nodes[right].Bounds.Max))
                throw new InvalidDataException("Invalid Mesh BVH child links or bounds.");
        }
        return order;
    }

    private static bool Contains(Aabb bounds, float3 position)
        => math.all(position >= bounds.Min) && math.all(position <= bounds.Max);

    private static byte[] HashPayload(ReadOnlySpan<float3> positions, ReadOnlySpan<MeshBvhTriangle> triangles,
        ReadOnlySpan<MeshBvhNode> nodes)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("SIAMESHBVHPAYLOAD"u8);
        Span<byte> bytes = stackalloc byte[36];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, positions.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[4..], triangles.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[8..], nodes.Length);
        hash.AppendData(bytes[..12]);
        foreach (var p in positions) { WriteVector(bytes, p); hash.AppendData(bytes[..12]); }
        foreach (var t in triangles) {
            WriteTriangle(bytes, t);
            BinaryPrimitives.WriteInt32LittleEndian(bytes[12..], t.SourceTriangle);
            BinaryPrimitives.WriteInt32LittleEndian(bytes[16..], t.MaterialSlot);
            hash.AppendData(bytes[..20]);
        }
        foreach (var node in nodes) {
            WriteVector(bytes, node.Bounds.Min); WriteVector(bytes[12..], node.Bounds.Max);
            BinaryPrimitives.WriteInt32LittleEndian(bytes[24..], node.EscapeIndex);
            BinaryPrimitives.WriteInt32LittleEndian(bytes[28..], node.FirstTriangle);
            BinaryPrimitives.WriteInt32LittleEndian(bytes[32..], node.TriangleCount);
            hash.AppendData(bytes);
        }
        return hash.GetHashAndReset();
    }

    private static string HashDecodedGeometry(ReadOnlySpan<float3> positions,
        ReadOnlySpan<MeshBvhTriangle> triangles, ReadOnlySpan<int> order)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("SIAMESHLOCALGEOMETRY"u8);
        Span<byte> bytes = stackalloc byte[16];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, positions.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[4..], triangles.Length);
        hash.AppendData(bytes[..8]);
        foreach (var p in positions) { WriteVector(bytes, p); hash.AppendData(bytes[..12]); }
        foreach (var index in order) { WriteTriangle(bytes, triangles[index]); hash.AppendData(bytes); }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void WriteTriangle(Span<byte> bytes, MeshBvhTriangle triangle)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, triangle.A);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[4..], triangle.B);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[8..], triangle.C);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[12..], triangle.MaterialSlot);
    }

    private static void WriteVector(Span<byte> bytes, float3 value)
    {
        BinaryPrimitives.WriteSingleLittleEndian(bytes, value.x);
        BinaryPrimitives.WriteSingleLittleEndian(bytes[4..], value.y);
        BinaryPrimitives.WriteSingleLittleEndian(bytes[8..], value.z);
    }
    private static void Vector(BinaryWriter writer, float3 value)
    { writer.Write(value.x); writer.Write(value.y); writer.Write(value.z); }
    private static float3 Vector(BinaryReader reader) => new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
}
