using System.Buffers.Binary;
using System.Security.Cryptography;
using Sia.Math;

namespace Sia.Engine.Mesh;

public sealed partial class MeshVirtualPageData
{
    public void Save(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _ = Write(stream, cancellationToken);
    }

    private string Write(Stream stream, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Span<byte> header = stackalloc byte[41];
        "SIAMSHVP"u8.CopyTo(header);
        header[8] = (byte)(HasBakeUvs ? 1 : 0);
        BinaryPrimitives.WriteInt32LittleEndian(header[9..], Vertices.Length);
        BinaryPrimitives.WriteInt32LittleEndian(header[13..], TriangleCount);
        var bounds = Bounds;
        Put(header, 17, bounds.Min.x); Put(header, 21, bounds.Min.y); Put(header, 25, bounds.Min.z);
        Put(header, 29, bounds.Max.x); Put(header, 33, bounds.Max.y); Put(header, 37, bounds.Max.z);
        stream.Write(header);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(header);
        Span<byte> buffer = stackalloc byte[32 * 1024];
        var writer = new MeshDataIO.Writer(stream, hash, buffer, token);
        for (var i = 0; i < Vertices.Length; i++) {
            var vertex = Vertices[i];
            writer.Vector(vertex.Position); writer.Vector(vertex.Normal);
            writer.Float(vertex.UV.x); writer.Float(vertex.UV.y);
            writer.Float(vertex.Tangent.x); writer.Float(vertex.Tangent.y); writer.Float(vertex.Tangent.z); writer.Float(vertex.Tangent.w);
            if (HasBakeUvs) { writer.Float(BakeUvs[i].x); writer.Float(BakeUvs[i].y); }
        }
        foreach (var index in Indices) writer.UInt(index);
        writer.Flush();
        var digest = hash.GetCurrentHash();
        token.ThrowIfCancellationRequested();
        stream.Write(digest); hash.AppendData(digest);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    public static MeshVirtualPageData Load(Stream stream, ulong maximumBytes = MaximumEncodedBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        cancellationToken.ThrowIfCancellationRequested();
        if (maximumBytes < 73) throw new InvalidDataException("Virtual page exceeds its byte budget.");
        Span<byte> header = stackalloc byte[41]; stream.ReadExactly(header);
        var flags = header[8];
        var vertices = BinaryPrimitives.ReadInt32LittleEndian(header[9..]);
        var triangles = BinaryPrimitives.ReadInt32LittleEndian(header[13..]);
        if (!header[..8].SequenceEqual("SIAMSHVP"u8) || flags > 1 || vertices is <= 0 or > MaximumTriangles * 3
            || triangles is <= 0 or > MaximumTriangles || vertices > triangles * 3)
            throw new InvalidDataException("Invalid virtual page header.");
        var encoded = 73ul + (ulong)vertices * (flags == 0 ? 48ul : 56ul) + (ulong)triangles * 12;
        var owned = (ulong)vertices * (ulong)(MeshVertex.Stride + (flags == 0 ? 0 : 8)) + (ulong)triangles * 12;
        if (encoded > maximumBytes || encoded > MaximumEncodedBytes || owned > maximumBytes
            || stream.CanSeek && stream.Length - stream.Position != (long)encoded - 41)
            throw new InvalidDataException("Virtual page exceeds its encoded/owned budget or available bytes.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); hash.AppendData(header);
        Span<byte> buffer = stackalloc byte[32 * 1024];
        var reader = new MeshDataIO.Reader(stream, hash, buffer, encoded - 73, cancellationToken);
        var values = new MeshVertex[vertices];
        var bakeUvs = flags == 0 ? [] : new float2[vertices];
        for (var i = 0; i < values.Length; i++) {
            values[i] = new(reader.Vector(), reader.Vector(), new(reader.Float(), reader.Float())) {
                Tangent = new(reader.Float(), reader.Float(), reader.Float(), reader.Float())
            };
            if (flags != 0) bakeUvs[i] = new(reader.Float(), reader.Float());
        }
        var indices = reader.UIntArray(triangles * 3);
        cancellationToken.ThrowIfCancellationRequested();
        Span<byte> footer = stackalloc byte[32]; stream.ReadExactly(footer);
        if (!CryptographicOperations.FixedTimeEquals(hash.GetCurrentHash(), footer))
            throw new InvalidDataException("Virtual page checksum mismatch.");
        hash.AppendData(footer);
        cancellationToken.ThrowIfCancellationRequested();
        if (stream.ReadByte() != -1) throw new InvalidDataException("Trailing virtual page data.");
        var bounds = new Aabb(new(Get(header, 17), Get(header, 21), Get(header, 25)), new(Get(header, 29), Get(header, 33), Get(header, 37)));
        try { return new(new(values, indices, bounds) { BakeUvs = bakeUvs }, Convert.ToHexString(hash.GetHashAndReset())); }
        catch (ArgumentException error) { throw new InvalidDataException("Invalid virtual page attributes.", error); }
    }

    private static void Put(Span<byte> bytes, int offset, float value) => BinaryPrimitives.WriteSingleLittleEndian(bytes[offset..], value);
    private static float Get(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadSingleLittleEndian(bytes[offset..]);
}
