using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using Sia.Asset;
using Sia.Math;

namespace Sia.Engine.Mesh;

public sealed partial class MeshManifest
{
    // One current format: fixed header, bounded optional products, and an unconditional checksum.
    public const int MaximumEncodedBytes = 93 + MaximumMaterialSlots * sizeof(int) + 4 * 48 + 32;

    /// <summary>Writes one current checksummed inventory; product presence never selects a different wire format.</summary>
    public void Save(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Span<byte> header = stackalloc byte[93]; "SIAMESH\0"u8.CopyTo(header);
        header[8] = (byte)((Raster is null ? 0 : 1) | (Bvh is null ? 0 : 2) | (HasBakeUvs ? 4 : 0)
            | (Virtual is null ? 0 : 8) | (RasterPages is null ? 0 : 16));
        Source.Value.TryWriteBytes(header[9..25], bigEndian: true, out _);
        Convert.FromHexString(SourceHash).CopyTo(header[25..57]);
        WriteSingle(header, 57, Bounds.Min.x); WriteSingle(header, 61, Bounds.Min.y); WriteSingle(header, 65, Bounds.Min.z);
        WriteSingle(header, 69, Bounds.Max.x); WriteSingle(header, 73, Bounds.Max.y); WriteSingle(header, 77, Bounds.Max.z);
        BinaryPrimitives.WriteInt32LittleEndian(header[81..], VertexCount);
        BinaryPrimitives.WriteInt32LittleEndian(header[85..], TriangleCount);
        BinaryPrimitives.WriteInt32LittleEndian(header[89..], _materialSlots.Length);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); hash.AppendData(header); stream.Write(header);
        Span<byte> scratch = stackalloc byte[1024]; var writer = new MeshDataIO.Writer(stream, hash, scratch, default);
        foreach (var slot in _materialSlots) writer.Int(slot);
        if (Raster is { } raster) WriteProduct(ref writer, raster);
        if (Bvh is { } bvh) WriteProduct(ref writer, bvh);
        if (Virtual is { } virtualGeometry) WriteProduct(ref writer, virtualGeometry);
        if (RasterPages is { } pages) WriteProduct(ref writer, pages);
        writer.Flush(); stream.Write(hash.GetHashAndReset());
    }

    /// <summary>Reads exactly one current inventory from a borrowed stream, including short/non-seekable reads.</summary>
    public static MeshManifest Load(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        try {
            Span<byte> header = stackalloc byte[93]; stream.ReadExactly(header[..8]);
            if (!header[..8].SequenceEqual("SIAMESH\0"u8)) throw new InvalidDataException("Unsupported Mesh manifest signature.");
            stream.ReadExactly(header[8..]);
            var flags = header[8]; var slotCount = BinaryPrimitives.ReadInt32LittleEndian(header[89..]);
            if ((flags & ~31) != 0 || slotCount is < 1 or > MaximumMaterialSlots)
                throw new InvalidDataException("Invalid Mesh manifest flags or slot count.");
            var bodyBytes = (ulong)slotCount * 4 + (ulong)BitOperations.PopCount((uint)(flags & 27)) * 48;
            if (93ul + bodyBytes + 32 > MaximumEncodedBytes || stream.CanSeek && stream.Length - stream.Position != (long)bodyBytes + 32)
                throw new InvalidDataException("Mesh manifest exceeds its metadata bound or declared payload length.");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); hash.AppendData(header);
            Span<byte> scratch = stackalloc byte[1024]; var reader = new MeshDataIO.Reader(stream, hash, scratch, bodyBytes, default);
            var slots = new int[slotCount]; for (var index = 0; index < slots.Length; index++) slots[index] = reader.Int();
            var raster = (flags & 1) != 0 ? ReadProduct<MeshRasterRecord>(ref reader) : null;
            var bvh = (flags & 2) != 0 ? ReadProduct<MeshBvhRecord>(ref reader) : null;
            var virtualGeometry = (flags & 8) != 0 ? ReadProduct<MeshVirtualRecord>(ref reader) : null;
            var pages = (flags & 16) != 0 ? ReadProduct<MeshRasterPagesRecord>(ref reader) : null;
            Span<byte> footer = stackalloc byte[32]; stream.ReadExactly(footer);
            if (!CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), footer))
                throw new InvalidDataException("Mesh manifest checksum mismatch.");
            if (stream.ReadByte() != -1) throw new InvalidDataException("Trailing Mesh manifest data.");
            return new(new(new Guid(header[9..25], bigEndian: true)), Convert.ToHexString(header[25..57]),
                new(new(Single(header, 57), Single(header, 61), Single(header, 65)), new(Single(header, 69), Single(header, 73), Single(header, 77))),
                BinaryPrimitives.ReadInt32LittleEndian(header[81..]), BinaryPrimitives.ReadInt32LittleEndian(header[85..]),
                (flags & 4) != 0, slots, raster, bvh, virtualGeometry, pages);
        }
        catch (EndOfStreamException error) { throw new InvalidDataException("Truncated Mesh manifest.", error); }
        catch (ArgumentException error) { throw new InvalidDataException("Invalid Mesh manifest metadata.", error); }
    }

    private static float Single(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadSingleLittleEndian(bytes[offset..]);
    private static void WriteSingle(Span<byte> bytes, int offset, float value) => BinaryPrimitives.WriteSingleLittleEndian(bytes[offset..], value);
    private static void WriteProduct<T>(ref MeshDataIO.Writer writer, MeshProductReference<T> product) where T : class, IAssetRecord
    {
        Span<byte> id = stackalloc byte[16]; product.Id.Value.TryWriteBytes(id, bigEndian: true, out _);
        writer.Bytes(id); writer.Bytes(Convert.FromHexString(product.BuildKey));
    }
    private static MeshProductReference<T> ReadProduct<T>(ref MeshDataIO.Reader reader) where T : class, IAssetRecord
    {
        Span<byte> id = stackalloc byte[16]; Span<byte> key = stackalloc byte[32]; reader.Bytes(id); reader.Bytes(key);
        return new(new(new Guid(id, bigEndian: true)), Convert.ToHexString(key));
    }
}
