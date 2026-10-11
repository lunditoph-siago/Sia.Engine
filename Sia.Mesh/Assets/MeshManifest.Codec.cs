using System.Text;
using Sia.Asset;
using Sia.Math;

namespace Sia.Engine.Mesh;

public sealed partial class MeshManifest
{
    // Fixed header 93 bytes, at most 4096 slot integers and three 48-byte product references.
    public const int MaximumEncodedBytes = 93 + MaximumMaterialSlots * sizeof(int) + 3 * 48;

    /// <summary>Writes one bounded manifest to a borrowed stream without product payloads.</summary>
    public void Save(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write("SIAMESH1"u8);
        writer.Write((byte)((Raster is null ? 0 : 1) | (Bvh is null ? 0 : 2) | (HasBakeUvs ? 4 : 0) | (Virtual is null ? 0 : 8)));
        writer.Write(Source.Value.ToByteArray(bigEndian: true));
        writer.Write(Convert.FromHexString(SourceHash));
        writer.Write(Bounds.Min.x); writer.Write(Bounds.Min.y); writer.Write(Bounds.Min.z);
        writer.Write(Bounds.Max.x); writer.Write(Bounds.Max.y); writer.Write(Bounds.Max.z);
        writer.Write(VertexCount);
        writer.Write(TriangleCount);
        writer.Write(_materialSlots.Length);
        foreach (var slot in _materialSlots) writer.Write(slot);
        if (Raster is { } raster) WriteProduct(writer, raster);
        if (Bvh is { } bvh) WriteProduct(writer, bvh);
        if (Virtual is { } virtualGeometry) WriteProduct(writer, virtualGeometry);
    }

    /// <summary>Reads exactly one bounded manifest from a borrowed stream, including short/non-seekable reads.</summary>
    public static MeshManifest Load(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Span<byte> magic = stackalloc byte[8];
        stream.ReadExactly(magic);
        if (!magic.SequenceEqual("SIAMESH1"u8)) throw new InvalidDataException("Unsupported Mesh manifest signature.");
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        var flags = reader.ReadByte();
        if ((flags & ~15) != 0) throw new InvalidDataException("Unknown Mesh manifest flags.");
        var source = ReadId(reader);
        var sourceHash = ReadHash(reader);
        var bounds = new Aabb(new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
            new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()));
        var vertexCount = reader.ReadInt32();
        var triangleCount = reader.ReadInt32();
        var slotCount = reader.ReadInt32();
        if (slotCount is < 1 or > MaximumMaterialSlots)
            throw new InvalidDataException("Mesh manifest material-slot count exceeds its metadata bound.");
        var slots = new int[slotCount];
        for (var i = 0; i < slots.Length; i++) slots[i] = reader.ReadInt32();
        var raster = (flags & 1) != 0 ? ReadProduct<MeshRasterRecord>(reader) : null;
        var bvh = (flags & 2) != 0 ? ReadProduct<MeshBvhRecord>(reader) : null;
        var virtualGeometry = (flags & 8) != 0 ? ReadProduct<MeshVirtualRecord>(reader) : null;
        if (stream.ReadByte() != -1) throw new InvalidDataException("Trailing Mesh manifest data.");
        try {
            return new(source, sourceHash, bounds, vertexCount, triangleCount, (flags & 4) != 0, slots, raster, bvh, virtualGeometry);
        }
        catch (ArgumentException exception) {
            throw new InvalidDataException("Invalid Mesh manifest metadata.", exception);
        }
    }

    private static void WriteProduct<TRecord>(BinaryWriter writer, MeshProductReference<TRecord> product)
        where TRecord : class, IAssetRecord
    {
        writer.Write(product.Id.Value.ToByteArray(bigEndian: true));
        writer.Write(Convert.FromHexString(product.BuildKey));
    }

    private static MeshProductReference<TRecord> ReadProduct<TRecord>(BinaryReader reader)
        where TRecord : class, IAssetRecord
    {
        var id = ReadId(reader);
        var hash = ReadHash(reader);
        if (!id.IsValid) throw new InvalidDataException("Product identity must be nonempty.");
        return new(id, hash);
    }

    private static AssetId ReadId(BinaryReader reader)
    {
        Span<byte> bytes = stackalloc byte[16];
        reader.BaseStream.ReadExactly(bytes);
        return new(new Guid(bytes, bigEndian: true));
    }

    private static string ReadHash(BinaryReader reader)
    {
        Span<byte> bytes = stackalloc byte[32];
        reader.BaseStream.ReadExactly(bytes);
        return Convert.ToHexString(bytes);
    }
}
