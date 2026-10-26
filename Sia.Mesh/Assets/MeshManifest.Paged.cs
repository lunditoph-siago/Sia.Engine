using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using Sia.Asset;
using Sia.Math;

namespace Sia.Engine.Mesh;

public sealed partial class MeshManifest
{
    private void SavePaged(Stream stream)
    {
        Span<byte> header = stackalloc byte[93];
        "SIAMESH2"u8.CopyTo(header);
        header[8] = (byte)(16 | (Raster is null ? 0 : 1) | (Bvh is null ? 0 : 2) | (HasBakeUvs ? 4 : 0) | (Virtual is null ? 0 : 8));
        Source.Value.TryWriteBytes(header[9..25], bigEndian: true, out _);
        Convert.FromHexString(SourceHash).CopyTo(header[25..57]);
        WriteSingle(header, 57, Bounds.Min.x); WriteSingle(header, 61, Bounds.Min.y); WriteSingle(header, 65, Bounds.Min.z);
        WriteSingle(header, 69, Bounds.Max.x); WriteSingle(header, 73, Bounds.Max.y); WriteSingle(header, 77, Bounds.Max.z);
        BinaryPrimitives.WriteInt32LittleEndian(header[81..], VertexCount);
        BinaryPrimitives.WriteInt32LittleEndian(header[85..], TriangleCount);
        BinaryPrimitives.WriteInt32LittleEndian(header[89..], _materialSlots.Length);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(header); stream.Write(header);
        Span<byte> scratch = stackalloc byte[1024];
        var writer = new MeshDataIO.Writer(stream, hash, scratch, default);
        foreach (var slot in _materialSlots) writer.Int(slot);
        if (Raster is { } raster) WritePagedProduct(ref writer, raster);
        if (Bvh is { } bvh) WritePagedProduct(ref writer, bvh);
        if (Virtual is { } virtualGeometry) WritePagedProduct(ref writer, virtualGeometry);
        WritePagedProduct(ref writer, RasterPages!);
        writer.Flush(); stream.Write(hash.GetHashAndReset());
    }

    private static MeshManifest LoadPaged(Stream stream, ReadOnlySpan<byte> magic)
    {
        try {
            Span<byte> header = stackalloc byte[93]; magic.CopyTo(header); stream.ReadExactly(header[8..]);
            var flags = header[8];
            var slotCount = BinaryPrimitives.ReadInt32LittleEndian(header[89..]);
            if ((flags & ~31) != 0 || (flags & 16) == 0 || slotCount is < 1 or > MaximumMaterialSlots)
                throw new InvalidDataException("Invalid paged Mesh manifest flags or slot count.");
            var bodyBytes = (ulong)slotCount * 4 + (ulong)BitOperations.PopCount((uint)(flags & 27)) * 48;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); hash.AppendData(header);
            Span<byte> scratch = stackalloc byte[1024];
            var reader = new MeshDataIO.Reader(stream, hash, scratch, bodyBytes, default);
            var slots = new int[slotCount];
            for (var index = 0; index < slots.Length; index++) slots[index] = reader.Int();
            var raster = (flags & 1) != 0 ? ReadPagedProduct<MeshRasterRecord>(ref reader) : null;
            var bvh = (flags & 2) != 0 ? ReadPagedProduct<MeshBvhRecord>(ref reader) : null;
            var virtualGeometry = (flags & 8) != 0 ? ReadPagedProduct<MeshVirtualRecord>(ref reader) : null;
            var pages = ReadPagedProduct<MeshRasterPagesRecord>(ref reader);
            Span<byte> footer = stackalloc byte[32]; stream.ReadExactly(footer);
            if (!CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), footer))
                throw new InvalidDataException("Paged Mesh manifest checksum mismatch.");
            if (stream.ReadByte() != -1) throw new InvalidDataException("Trailing Mesh manifest data.");
            return new(new(new Guid(header[9..25], bigEndian: true)), Convert.ToHexString(header[25..57]),
                new(new(Single(header, 57), Single(header, 61), Single(header, 65)),
                    new(Single(header, 69), Single(header, 73), Single(header, 77))),
                BinaryPrimitives.ReadInt32LittleEndian(header[81..]), BinaryPrimitives.ReadInt32LittleEndian(header[85..]),
                (flags & 4) != 0, slots, raster, bvh, virtualGeometry, pages);
        }
        catch (EndOfStreamException error) { throw new InvalidDataException("Truncated paged Mesh manifest.", error); }
        catch (ArgumentException error) { throw new InvalidDataException("Invalid paged Mesh manifest metadata.", error); }
    }

    private static float Single(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadSingleLittleEndian(bytes[offset..]);
    private static void WriteSingle(Span<byte> bytes, int offset, float value) => BinaryPrimitives.WriteSingleLittleEndian(bytes[offset..], value);
    private static void WritePagedProduct<T>(ref MeshDataIO.Writer writer, MeshProductReference<T> product) where T : class, IAssetRecord
    {
        Span<byte> id = stackalloc byte[16]; product.Id.Value.TryWriteBytes(id, bigEndian: true, out _);
        writer.Bytes(id); writer.Bytes(Convert.FromHexString(product.BuildKey));
    }
    private static MeshProductReference<T> ReadPagedProduct<T>(ref MeshDataIO.Reader reader) where T : class, IAssetRecord
    {
        Span<byte> id = stackalloc byte[16]; Span<byte> key = stackalloc byte[32];
        reader.Bytes(id); reader.Bytes(key);
        return new(new(new Guid(id, bigEndian: true)), Convert.ToHexString(key));
    }
}
