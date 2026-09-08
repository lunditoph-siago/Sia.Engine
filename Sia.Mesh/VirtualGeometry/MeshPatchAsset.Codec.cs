using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using Sia.Math;

namespace Sia.Engine.Mesh;

public sealed partial class MeshPatchAsset
{
    private const int HeaderSize = 272;
    private const int HashOffset = 56;
    private const int HashSize = 32;
    private static ReadOnlySpan<int> Strides => [56, 48, 4, 72, 4, 1, 4];

    private static int SectionStride(int version, int section) => section == 1 && version == 1 ? 32 : Strides[section];

    public byte[] Encode(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var tree = Build.Tree;
        var (geometry, meshlets) = tree.CopyGeometry();
        int[] counts = [tree.Nodes.Length, geometry.Vertices.Length, geometry.Indices.Length,
            meshlets.Meshlets.Length, meshlets.VertexIndices.Length, meshlets.TriangleIndices.Length,
            meshlets.SourceTriangleIndices.Length];
        var length = HeaderSize;
        for (var i = 0; i < counts.Length; i++) { length = checked(length + counts[i] * Strides[i]); }
        var bytes = new byte[length];
        "SIAPATCH"u8.CopyTo(bytes);
        var writer = new Writer(bytes.AsSpan(8));
        writer.Int(FormatVersion);
        writer.Int(BuilderVersion);
        writer.Long(length);
        Convert.FromHexString(SourceHash).CopyTo(bytes, 24);
        writer = new(bytes.AsSpan(88));
        writer.Int(Settings.MaxLeafTriangles);
        writer.Int(Settings.MaxChildren);
        writer.Float(Settings.ParentTriangleRatio);
        writer.Float(Settings.NormalWeight);
        writer.Float(Settings.UVWeight);
        writer.Int(Build.SourceTriangleCount);
        writer.Int(Build.RemovedDegenerateTriangleCount);
        writer.Int(Build.SimplificationCount);
        writer.Int(Build.TargetMissCount);
        writer.Int(Build.UnreducedGroupCount);
        writer.Int(tree.RootCount);
        writer.Int(tree.FinestTriangleCount);
        writer.Box(geometry.Bounds);
        long offset = HeaderSize;
        for (var i = 0; i < counts.Length; i++) {
            writer.Long(offset);
            writer.Int(counts[i]);
            writer.Int(Strides[i]);
            offset += (long)counts[i] * Strides[i];
        }
        writer = new(bytes.AsSpan(HeaderSize));
        foreach (var node in tree.Nodes.Span) {
            cancellationToken.ThrowIfCancellationRequested();
            writer.Box(node.Bounds);
            writer.Float(node.EstimatedSpatialError);
            writer.Int(node.Parent);
            writer.Int(node.ChildOffset);
            writer.Int(node.ChildCount);
            writer.Int(node.MeshletOffset);
            writer.Int(node.MeshletCount);
            writer.Int(node.TriangleOffset);
            writer.Int(node.TriangleCount);
        }
        foreach (var vertex in geometry.Vertices) {
            cancellationToken.ThrowIfCancellationRequested();
            writer.Vertex(vertex);
        }
        foreach (var index in geometry.Indices) { writer.UInt(index); }
        foreach (var meshlet in meshlets.Meshlets) {
            cancellationToken.ThrowIfCancellationRequested();
            writer.Int(meshlet.VertexOffset);
            writer.Int(meshlet.VertexCount);
            writer.Int(meshlet.TriangleOffset);
            writer.Int(meshlet.TriangleCount);
            writer.Box(meshlet.Bounds.Box);
            writer.Vector(meshlet.Bounds.Center);
            writer.Float(meshlet.Bounds.Radius);
            writer.Vector(meshlet.Bounds.ConeAxis);
            writer.Float(meshlet.Bounds.ConeCutoff);
        }
        foreach (var index in meshlets.VertexIndices) { writer.UInt(index); }
        writer.Bytes(meshlets.TriangleIndices);
        foreach (var index in meshlets.SourceTriangleIndices) { writer.UInt(index); }
        cancellationToken.ThrowIfCancellationRequested();
        Hash(bytes).CopyTo(bytes, HashOffset);
        return bytes;
    }

    public static MeshPatchAsset Decode(ReadOnlySpan<byte> bytes, CancellationToken cancellationToken = default,
        int maximumDecodedBytes = 512 * 1024 * 1024)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfNegative(maximumDecodedBytes);
        if (bytes.StartsWith("SIAGZIP1"u8)) {
            return DecodeRaw(Decompress(bytes, maximumDecodedBytes, cancellationToken), cancellationToken);
        }
        Require(bytes.Length <= maximumDecodedBytes, "Patch asset exceeds the decoded byte limit.");
        return DecodeRaw(bytes, cancellationToken);
    }

    private static MeshPatchAsset DecodeRaw(ReadOnlySpan<byte> bytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Require(bytes.Length >= HeaderSize && bytes[..8].SequenceEqual("SIAPATCH"u8), "Invalid patch asset header.");
        var reader = new Reader(bytes[8..]);
        var version = reader.Int();
        Require(version is 1 or FormatVersion, "Unsupported patch asset format version.");
        var builderVersion = reader.Int();
        Require(builderVersion > 0 && reader.Long() == bytes.Length, "Invalid patch asset version or length.");
        Require(CryptographicOperations.FixedTimeEquals(Hash(bytes), bytes.Slice(HashOffset, HashSize)), "Patch asset checksum mismatch.");
        var sourceHash = Convert.ToHexString(bytes.Slice(24, HashSize));
        reader = new(bytes[88..]);
        var settings = new MeshPatchBuildSettings(reader.Int(), reader.Int(), reader.Float(), reader.Float(), reader.Float());
        Require(settings.MaxLeafTriangles is >= 1 and <= 512 && settings.MaxChildren is >= 2 and <= 8
            && float.IsFinite(settings.ParentTriangleRatio) && settings.ParentTriangleRatio > 0 && settings.ParentTriangleRatio < 1
            && float.IsFinite(settings.NormalWeight) && settings.NormalWeight >= 0
            && float.IsFinite(settings.UVWeight) && settings.UVWeight >= 0, "Invalid patch build settings.");
        var sourceTriangles = reader.Int();
        var removed = reader.Int();
        var simplified = reader.Int();
        var targetMisses = reader.Int();
        var unreduced = reader.Int();
        var roots = reader.Int();
        var finest = reader.Int();
        var bounds = reader.Box();
        Span<int> counts = stackalloc int[7];
        long offset = HeaderSize;
        for (var i = 0; i < counts.Length; i++) {
            Require(reader.Long() == offset, "Patch sections must form a contiguous ordered stream.");
            counts[i] = reader.Int();
            var stride = SectionStride(version, i);
            Require(counts[i] >= 0 && reader.Int() == stride, "Invalid patch section count or stride.");
            offset += (long)counts[i] * stride;
            Require(offset <= bytes.Length, "Patch section exceeds the asset length.");
        }
        Require(offset == bytes.Length, "Unexpected data after patch sections.");
        Require(sourceTriangles >= 0 && removed >= 0 && removed <= sourceTriangles && finest == sourceTriangles - removed
            && simplified >= 0 && simplified <= counts[0] && targetMisses >= 0 && unreduced >= 0,
            "Invalid patch build diagnostics.");
        Require(counts[2] % 3 == 0 && counts[5] == counts[2] && counts[6] == counts[2] / 3,
            "Meshlet streams must cover the geometry triangles.");
        reader = new(bytes[HeaderSize..]);
        var nodes = new MeshPatchNode[counts[0]];
        for (var i = 0; i < nodes.Length; i++) {
            cancellationToken.ThrowIfCancellationRequested();
            nodes[i] = new(reader.Box(), reader.Float(), reader.Int(), reader.Int(), reader.Int(),
                reader.Int(), reader.Int(), reader.Int(), reader.Int());
        }
        var vertices = new MeshVertex[counts[1]];
        for (var i = 0; i < vertices.Length; i++) {
            cancellationToken.ThrowIfCancellationRequested();
            vertices[i] = reader.Vertex(version);
        }
        var indices = reader.UIntArray(counts[2]);
        var clusters = new Meshlet[counts[3]];
        for (var i = 0; i < clusters.Length; i++) {
            cancellationToken.ThrowIfCancellationRequested();
            clusters[i] = new(reader.Int(), reader.Int(), reader.Int(), reader.Int(),
                new(reader.Box(), reader.Vector(), reader.Float(), reader.Vector(), reader.Float()));
        }
        var references = reader.UIntArray(counts[4]);
        var localIndices = reader.Bytes(counts[5]).ToArray();
        var sourceIndices = reader.UIntArray(counts[6]);
        var tree = MeshPatchTree.Restore(nodes, roots, finest, new(vertices, indices, bounds),
            new(clusters, references, localIndices, sourceIndices), cancellationToken);
        Require(simplified == nodes.Count(node => node.ChildCount != 0), "Simplification count differs from the patch hierarchy.");
        return new(new(tree, sourceTriangles, removed, simplified, targetMisses, unreduced), settings, sourceHash, builderVersion);
    }

    private static byte[] Hash(ReadOnlySpan<byte> bytes)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(bytes[..HashOffset]);
        hash.AppendData(bytes[(HashOffset + HashSize)..]);
        return hash.GetHashAndReset();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) { throw new InvalidDataException(message); }
    }

    private ref struct Writer(Span<byte> bytes)
    {
        private Span<byte> _remaining = bytes;
        public void Int(int value) { BinaryPrimitives.WriteInt32LittleEndian(_remaining, value); _remaining = _remaining[4..]; }
        public void UInt(uint value) { BinaryPrimitives.WriteUInt32LittleEndian(_remaining, value); _remaining = _remaining[4..]; }
        public void Long(long value) { BinaryPrimitives.WriteInt64LittleEndian(_remaining, value); _remaining = _remaining[8..]; }
        public void Float(float value) => Int(BitConverter.SingleToInt32Bits(value));
        public void Vector(float3 value) { Float(value.x); Float(value.y); Float(value.z); }
        public void Box(Aabb value) { Vector(value.Min); Vector(value.Max); }
        public void Vertex(MeshVertex value)
        {
            Vector(value.Position); Vector(value.Normal); Float(value.UV.x); Float(value.UV.y);
            Float(value.Tangent.x); Float(value.Tangent.y); Float(value.Tangent.z); Float(value.Tangent.w);
        }
        public void Bytes(ReadOnlySpan<byte> value) { value.CopyTo(_remaining); _remaining = _remaining[value.Length..]; }
    }

    private ref struct Reader(ReadOnlySpan<byte> bytes)
    {
        private ReadOnlySpan<byte> _remaining = bytes;
        public int Int() { var value = BinaryPrimitives.ReadInt32LittleEndian(_remaining); _remaining = _remaining[4..]; return value; }
        public uint UInt() { var value = BinaryPrimitives.ReadUInt32LittleEndian(_remaining); _remaining = _remaining[4..]; return value; }
        public long Long() { var value = BinaryPrimitives.ReadInt64LittleEndian(_remaining); _remaining = _remaining[8..]; return value; }
        public float Float() => BitConverter.Int32BitsToSingle(Int());
        public float3 Vector() => new(Float(), Float(), Float());
        public Aabb Box() => new(Vector(), Vector());
        public MeshVertex Vertex(int version)
        {
            var vertex = new MeshVertex(Vector(), Vector(), new(Float(), Float()));
            return version == 1 ? vertex : vertex with { Tangent = new(Float(), Float(), Float(), Float()) };
        }
        public uint[] UIntArray(int count)
        {
            var bytes = Bytes(checked(count * 4));
            var values = new uint[count];
            if (BitConverter.IsLittleEndian) { MemoryMarshal.Cast<byte, uint>(bytes).CopyTo(values); }
            else { for (var i = 0; i < count; i++) { values[i] = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(i * 4)..]); } }
            return values;
        }
        public ReadOnlySpan<byte> Bytes(int count) { var value = _remaining[..count]; _remaining = _remaining[count..]; return value; }
    }
}
