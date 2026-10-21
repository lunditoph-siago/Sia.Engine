using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Sia.Math;

namespace Sia.Engine.Mesh;

public readonly record struct MeshBvhBuildSettings(int MaxLeafTriangles)
{
    public static MeshBvhBuildSettings Default => new(8);
}

/// <summary>Indices into mesh-local positions, with stable source primitive and material-slot identity.</summary>
public readonly record struct MeshBvhTriangle(uint A, uint B, uint C, int SourceTriangle, int MaterialSlot);

/// <summary>Preorder node; EscapeIndex skips its complete subtree. Internal nodes have zero TriangleCount.</summary>
public readonly record struct MeshBvhNode(Aabb Bounds, int EscapeIndex, int FirstTriangle, int TriangleCount);

/// <summary>Independent mesh-local geometry acceleration without instance transforms or material values.</summary>
public sealed partial class MeshBvhData
{
    private readonly float3[] _positions;
    private readonly MeshBvhTriangle[] _triangles;
    private readonly MeshBvhNode[] _nodes;

    public ReadOnlySpan<float3> Positions => _positions;
    public ReadOnlySpan<MeshBvhTriangle> Triangles => _triangles;
    public ReadOnlySpan<MeshBvhNode> Nodes => _nodes;
    public Aabb Bounds => _nodes[0].Bounds;
    public MeshBvhBuildSettings Settings { get; }
    public string GeometryHash { get; }
    public string BuildKey { get; }
    public ulong PayloadBytes { get; }

    private MeshBvhData(float3[] positions, MeshBvhTriangle[] triangles, MeshBvhNode[] nodes,
        MeshBvhBuildSettings settings, string geometryHash)
    {
        _positions = positions;
        _triangles = triangles;
        _nodes = nodes;
        Settings = settings;
        GeometryHash = geometryHash;
        BuildKey = ComputeBuildKey(geometryHash, settings);
        PayloadBytes = GeometryBytes(positions.Length, triangles.Length)
            + (ulong)nodes.Length * (ulong)Unsafe.SizeOf<MeshBvhNode>();
    }

    private static string ComputeBuildKey(string geometryHash, MeshBvhBuildSettings settings)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("SIAMESHBVH2"u8);
        hash.AppendData(Convert.FromHexString(geometryHash));
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, settings.MaxLeafTriangles);
        hash.AppendData(bytes);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    public static MeshBvhData Build(MeshSource source, MeshBvhBuildSettings? settings = null,
        ulong maximumBytes = 128ul * 1024 * 1024, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var options = settings ?? MeshBvhBuildSettings.Default;
        if (options.MaxLeafTriangles is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(settings));
        cancellationToken.ThrowIfCancellationRequested();
        var geometryBytes = GeometryBytes(source.Vertices.Length, source.Indices.Length / 3);
        if (geometryBytes + (ulong)Unsafe.SizeOf<MeshBvhNode>() > maximumBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes), "Mesh BVH geometry cannot fit the payload budget.");
        var builder = new Builder(source, options, maximumBytes, geometryBytes, cancellationToken);
        return builder.Build();
    }

    private static ulong GeometryBytes(int vertices, int triangles)
        => (ulong)vertices * (ulong)Unsafe.SizeOf<float3>()
            + (ulong)triangles * (ulong)Unsafe.SizeOf<MeshBvhTriangle>();

    private static string HashGeometry(MeshSource source, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("SIAMESHLOCALGEOMETRY2"u8);
        Span<byte> bytes = stackalloc byte[16];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, source.Vertices.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[4..], source.Indices.Length / 3);
        hash.AppendData(bytes[..8]);
        var index = 0;
        foreach (var vertex in source.Vertices) {
            if ((index++ & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            BinaryPrimitives.WriteSingleLittleEndian(bytes, vertex.Position.x);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[4..], vertex.Position.y);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[8..], vertex.Position.z);
            hash.AppendData(bytes[..12]);
        }
        foreach (var section in source.Sections) {
            for (var i = section.FirstIndex; i < section.FirstIndex + section.IndexCount; i += 3) {
                if ((index++ & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                BinaryPrimitives.WriteUInt32LittleEndian(bytes, source.Indices[i]);
                BinaryPrimitives.WriteUInt32LittleEndian(bytes[4..], source.Indices[i + 1]);
                BinaryPrimitives.WriteUInt32LittleEndian(bytes[8..], source.Indices[i + 2]);
                BinaryPrimitives.WriteInt32LittleEndian(bytes[12..], section.MaterialSlot);
                hash.AppendData(bytes);
            }
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
