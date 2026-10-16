using System.Buffers.Binary;
using System.Security.Cryptography;
using Sia.Asset;
using Sia.Math;

namespace Sia.Engine.Mesh;

public sealed partial class MeshVirtualPagesData
{
    private const int HeaderBytes = 157;

    public void Save(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        cancellationToken.ThrowIfCancellationRequested();
        Span<byte> header = stackalloc byte[HeaderBytes];
        "SIAMSHVI"u8.CopyTo(header);
        ProductId.Value.TryWriteBytes(header.Slice(8, 16), bigEndian: true, out _);
        Convert.FromHexString(SourceHash).CopyTo(header[24..]);
        Convert.FromHexString(BuildKey).CopyTo(header[56..]);
        PutInt(header, 88, SourceVertexCount); PutInt(header, 92, SourceTriangleCount);
        header[96] = (byte)(HasBakeUvs ? 1 : 0);
        PutFloat(header, 97, Bounds.Min.x); PutFloat(header, 101, Bounds.Min.y); PutFloat(header, 105, Bounds.Min.z);
        PutFloat(header, 109, Bounds.Max.x); PutFloat(header, 113, Bounds.Max.y); PutFloat(header, 117, Bounds.Max.z);
        PutInt(header, 121, Settings.MaxLeafTriangles); PutInt(header, 125, Settings.MaxChildren);
        PutFloat(header, 129, Settings.ParentTriangleRatio); PutFloat(header, 133, Settings.NormalWeight); PutFloat(header, 137, Settings.UVWeight);
        PutInt(header, 141, _parts.Length); PutInt(header, 145, _nodes.Length);
        PutInt(header, 149, _ranges.Length); PutInt(header, 153, _pages.Length);
        stream.Write(header);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); hash.AppendData(header);
        Span<byte> buffer = stackalloc byte[32 * 1024];
        var writer = new MeshDataIO.Writer(stream, hash, buffer, cancellationToken);
        foreach (var part in _parts) {
            writer.Int(part.MaterialSlot); writer.Int(part.FirstSourceTriangle); writer.Int(part.SourceTriangleCount);
            writer.Int(part.FirstNode); writer.Int(part.NodeCount); writer.Int(part.RootCount); writer.Int(part.FinestTriangleCount);
        }
        foreach (var node in _nodes) {
            writer.Box(node.Bounds); writer.Float(node.Error); writer.Int(node.Parent); writer.Int(node.FirstChild);
            writer.Int(node.ChildCount); writer.Int(node.FirstRange); writer.Int(node.RangeCount); writer.Int(node.TriangleCount);
        }
        foreach (var range in _ranges) {
            writer.Int(range.Node); writer.Int(range.Page); writer.Int(range.FirstTriangle); writer.Int(range.TriangleCount);
        }
        Span<byte> id = stackalloc byte[16];
        foreach (var page in _pages) {
            page.Id.Value.TryWriteBytes(id, bigEndian: true, out _); writer.Bytes(id);
            writer.Bytes(Convert.FromHexString(page.ContentHash));
            writer.Int(page.EncodedBytes); writer.Int(page.VertexCount); writer.Int(page.TriangleCount); writer.Box(page.Bounds);
        }
        writer.Flush();
        cancellationToken.ThrowIfCancellationRequested();
        stream.Write(hash.GetHashAndReset());
    }

    public static MeshVirtualPagesData Load(Stream stream, ulong maximumBytes = 128ul * 1024 * 1024,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream); cancellationToken.ThrowIfCancellationRequested();
        if (maximumBytes < HeaderBytes + 32) throw new InvalidDataException("Virtual index exceeds its byte budget.");
        Span<byte> header = stackalloc byte[HeaderBytes]; stream.ReadExactly(header);
        var vertices = GetInt(header, 88); var triangles = GetInt(header, 92); var flags = header[96];
        var parts = GetInt(header, 141); var nodes = GetInt(header, 145); var ranges = GetInt(header, 149); var pages = GetInt(header, 153);
        Require(header[..8].SequenceEqual("SIAMSHVI"u8) && vertices > 0 && triangles > 0 && flags <= 1
            && parts is > 0 and <= MaximumParts && nodes is >= 0 and <= MaximumNodes
            && ranges is >= 0 and <= MaximumRanges && pages is >= 0 and <= MaximumPages, "Invalid virtual index header.");
        var encoded = WireBytes(parts, nodes, ranges, pages);
        Require(encoded <= maximumBytes && encoded <= MaximumEncodedBytes && OwnedBytes(parts, nodes, ranges, pages) <= maximumBytes
            && (!stream.CanSeek || stream.Length - stream.Position == (long)encoded - HeaderBytes),
            "Virtual index exceeds its encoded/owned budget or available bytes.");
        var product = new AssetId(new Guid(header.Slice(8, 16), bigEndian: true));
        var sourceHash = Convert.ToHexString(header.Slice(24, 32));
        var key = Convert.ToHexString(header.Slice(56, 32));
        var settings = new MeshPatchBuildSettings(GetInt(header, 121), GetInt(header, 125),
            GetFloat(header, 129), GetFloat(header, 133), GetFloat(header, 137));
        var bounds = new Aabb(new(GetFloat(header, 97), GetFloat(header, 101), GetFloat(header, 105)),
            new(GetFloat(header, 109), GetFloat(header, 113), GetFloat(header, 117)));
        Require(product.IsValid && settings.IsValid && Valid(bounds) && key == ComputeBuildKey(sourceHash, settings),
            "Virtual index identity, settings or bounds are invalid.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); hash.AppendData(header);
        Span<byte> buffer = stackalloc byte[32 * 1024];
        var reader = new MeshDataIO.Reader(stream, hash, buffer, encoded - HeaderBytes - 32, cancellationToken);
        var partValues = new MeshVirtualPagePart[parts]; var nodeValues = new MeshVirtualPageNode[nodes];
        var rangeValues = new MeshVirtualPageRange[ranges]; var pageValues = new MeshVirtualPageDescriptor[pages];
        for (var i = 0; i < parts; i++) partValues[i] = new(reader.Int(), reader.Int(), reader.Int(), reader.Int(), reader.Int(), reader.Int(), reader.Int());
        for (var i = 0; i < nodes; i++) nodeValues[i] = new(reader.Box(), reader.Float(), reader.Int(), reader.Int(), reader.Int(), reader.Int(), reader.Int(), reader.Int());
        for (var i = 0; i < ranges; i++) rangeValues[i] = new(reader.Int(), reader.Int(), reader.Int(), reader.Int());
        Span<byte> id = stackalloc byte[16]; Span<byte> digest = stackalloc byte[32];
        for (var i = 0; i < pages; i++) {
            reader.Bytes(id); reader.Bytes(digest);
            pageValues[i] = new(new(new Guid(id, bigEndian: true)), Convert.ToHexString(digest),
                reader.Int(), reader.Int(), reader.Int(), reader.Box());
        }
        cancellationToken.ThrowIfCancellationRequested();
        stream.ReadExactly(digest);
        Require(CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), digest), "Virtual index checksum mismatch.");
        cancellationToken.ThrowIfCancellationRequested();
        Require(stream.ReadByte() == -1, "Trailing virtual index data.");
        return new(product, sourceHash, settings, vertices, triangles, flags != 0, bounds,
            partValues, nodeValues, rangeValues, pageValues, cancellationToken);
    }

    private static void PutInt(Span<byte> bytes, int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(bytes[offset..], value);
    private static void PutFloat(Span<byte> bytes, int offset, float value) => BinaryPrimitives.WriteSingleLittleEndian(bytes[offset..], value);
    private static int GetInt(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadInt32LittleEndian(bytes[offset..]);
    private static float GetFloat(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadSingleLittleEndian(bytes[offset..]);
}
