using System.Buffers.Binary;
using System.Security.Cryptography;
using Sia.Math;

namespace Sia.Engine.Mesh;

public sealed partial class MeshRasterPageData
{
    public void Save(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream); _ = Write(stream, cancellationToken);
    }

    private string Write(Stream stream, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Span<byte> header = stackalloc byte[32]; "SIAMSHRP"u8.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], (int)Kind);
        BinaryPrimitives.WriteUInt32LittleEndian(header[12..], HasBakeUvs ? 1u : 0);
        BinaryPrimitives.WriteInt32LittleEndian(header[16..], ElementCount);
        BinaryPrimitives.WriteUInt32LittleEndian(header[20..], 0);
        BinaryPrimitives.WriteUInt64LittleEndian(header[24..], (ulong)EncodedBytes - 64);
        stream.Write(header);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); hash.AppendData(header);
        Span<byte> buffer = stackalloc byte[32 * 1024]; var writer = new MeshDataIO.Writer(stream, hash, buffer, token);
        for (var index = 0; index < _vertices.Length; index++) {
            writer.Vertex(_vertices[index]);
            if (HasBakeUvs) { writer.Float(_bakeUvs[index].x); writer.Float(_bakeUvs[index].y); }
        }
        foreach (var index in _indices) writer.UInt(index);
        writer.Flush(); token.ThrowIfCancellationRequested();
        var digest = hash.GetCurrentHash(); stream.Write(digest); hash.AppendData(digest);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    public static MeshRasterPageData Load(Stream stream, ulong maximumBytes = DefaultMaximumBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream); cancellationToken.ThrowIfCancellationRequested();
        if (maximumBytes < 64) throw new InvalidDataException("Raster page framing exceeds its byte budget.");
        try {
            Span<byte> header = stackalloc byte[32]; stream.ReadExactly(header);
            var kind = (MeshRasterPageKind)BinaryPrimitives.ReadInt32LittleEndian(header[8..]);
            var flags = BinaryPrimitives.ReadUInt32LittleEndian(header[12..]);
            var count = BinaryPrimitives.ReadInt32LittleEndian(header[16..]);
            if (!header[..8].SequenceEqual("SIAMSHRP"u8) || flags > 1 || BinaryPrimitives.ReadUInt32LittleEndian(header[20..]) != 0
                || (kind == MeshRasterPageKind.Vertices ? count is <= 0 or > MaximumVertices
                    : kind != MeshRasterPageKind.Indices || flags != 0 || count is <= 0 or > MaximumTriangles * 3 || count % 3 != 0))
                throw new InvalidDataException("Invalid Raster page header.");
            var vertices = kind == MeshRasterPageKind.Vertices ? count : 0;
            var indices = kind == MeshRasterPageKind.Indices ? count : 0;
            RequireBudget(vertices, indices, flags != 0, maximumBytes);
            var encoded = 64ul + (ulong)vertices * (flags == 0 ? 48ul : 56ul) + (ulong)indices * 4;
            if (BinaryPrimitives.ReadUInt64LittleEndian(header[24..]) != encoded - 64
                || stream.CanSeek && stream.Length - stream.Position != (long)encoded - 32)
                throw new InvalidDataException("Raster page payload length mismatch.");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); hash.AppendData(header);
            Span<byte> buffer = stackalloc byte[32 * 1024];
            var reader = new MeshDataIO.Reader(stream, hash, buffer, encoded - 64, cancellationToken);
            var values = new MeshVertex[vertices]; var bakeUvs = flags == 0 ? [] : new float2[vertices];
            for (var index = 0; index < vertices; index++) {
                values[index] = new(reader.Vector(), reader.Vector(), new(reader.Float(), reader.Float())) {
                    Tangent = new(reader.Float(), reader.Float(), reader.Float(), reader.Float())
                };
                if (flags != 0) bakeUvs[index] = new(reader.Float(), reader.Float());
            }
            var indexValues = reader.UIntArray(indices);
            cancellationToken.ThrowIfCancellationRequested(); Span<byte> footer = stackalloc byte[32]; stream.ReadExactly(footer);
            if (!CryptographicOperations.FixedTimeEquals(hash.GetCurrentHash(), footer)) throw new InvalidDataException("Raster page checksum mismatch.");
            hash.AppendData(footer); cancellationToken.ThrowIfCancellationRequested();
            if (stream.ReadByte() != -1) throw new InvalidDataException("Trailing Raster page data.");
            return new(kind, values, bakeUvs, indexValues, Convert.ToHexString(hash.GetHashAndReset()), cancellationToken);
        }
        catch (EndOfStreamException error) { throw new InvalidDataException("Truncated Raster page.", error); }
    }
}
