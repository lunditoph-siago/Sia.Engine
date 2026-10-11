using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using Sia.Math;

namespace Sia.Engine.Mesh;

public sealed partial class MeshVirtualData
{
    private const int HeaderBytes = 161;
    private const int PartHeaderBytes = 44;

    /// <summary>Writes independent virtual geometry to a borrowed stream; callers own partial output on failure.</summary>
    public void Save(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        cancellationToken.ThrowIfCancellationRequested();
        var header = CreateHeader();
        stream.Write(header);
        Span<byte> descriptor = stackalloc byte[PartHeaderBytes];
        foreach (var part in _parts) {
            var payload = part.Patches.Encode(cancellationToken);
            BinaryPrimitives.WriteInt32LittleEndian(descriptor, part.MaterialSlot);
            BinaryPrimitives.WriteInt32LittleEndian(descriptor[4..], part.FirstSourceTriangle);
            BinaryPrimitives.WriteInt32LittleEndian(descriptor[8..], payload.Length);
            PartHash(header.AsSpan(129, 32), descriptor[..12], payload).CopyTo(descriptor[12..]);
            stream.Write(descriptor);
            stream.Write(payload);
        }
    }

    /// <summary>Reads one independent virtual product without loading source, Raster or BVH.</summary>
    public static MeshVirtualData Load(Stream stream, ulong maximumBytes = 128ul * 1024 * 1024,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        cancellationToken.ThrowIfCancellationRequested();
        if (maximumBytes < HeaderBytes) throw new InvalidDataException("Virtual Mesh exceeds its byte budget.");
        Span<byte> header = stackalloc byte[HeaderBytes];
        stream.ReadExactly(header);
        if (!header[..8].SequenceEqual("SIAMSHV1"u8)
            || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(header[..129]), header[129..]))
            throw new InvalidDataException("Virtual Mesh header signature/checksum mismatch.");
        var flags = header[8];
        var settings = new MeshPatchBuildSettings(Int(header, 9), Int(header, 13), Float(header, 17), Float(header, 21), Float(header, 25));
        var vertices = Int(header, 29); var triangles = Int(header, 33); var count = Int(header, 37);
        var bounds = new Aabb(new(Float(header, 41), Float(header, 45), Float(header, 49)),
            new(Float(header, 53), Float(header, 57), Float(header, 61)));
        var sourceHash = Convert.ToHexString(header.Slice(65, 32));
        if (flags > 1 || !settings.IsValid || vertices <= 0 || triangles <= 0 || count is < 1 or > MaximumParts
            || !math.all(math.isfinite(bounds.Min)) || !math.all(math.isfinite(bounds.Max)) || math.any(bounds.Min > bounds.Max)
            || Convert.ToHexString(header.Slice(97, 32)) != ComputeBuildKey(sourceHash, settings))
            throw new InvalidDataException("Invalid virtual Mesh metadata or build key.");
        var owned = PartArrayBytes(count);
        if (owned > maximumBytes || HeaderBytes + (ulong)count * (PartHeaderBytes + 268) > maximumBytes)
            throw new InvalidDataException("Virtual Mesh parts exceed the byte budget.");
        var parts = new MeshVirtualPart[count];
        var encoded = (ulong)HeaderBytes;
        long covered = 0;
        Span<byte> descriptor = stackalloc byte[PartHeaderBytes];
        for (var i = 0; i < count; i++) {
            cancellationToken.ThrowIfCancellationRequested();
            stream.ReadExactly(descriptor);
            var slot = Int(descriptor, 0); var first = Int(descriptor, 4); var length = Int(descriptor, 8);
            if (slot < 0 || first != covered || length < 268)
                throw new InvalidDataException("Invalid virtual Mesh part descriptor.");
            encoded += PartHeaderBytes + (ulong)length;
            if (encoded > maximumBytes || (stream.CanSeek && stream.Length - stream.Position < length))
                throw new InvalidDataException("Virtual Mesh encoded part exceeds its budget or available bytes.");
            var rented = ArrayPool<byte>.Shared.Rent(length);
            try {
                var payload = rented.AsSpan(0, length);
                stream.ReadExactly(payload);
                cancellationToken.ThrowIfCancellationRequested();
                if (!CryptographicOperations.FixedTimeEquals(PartHash(header[129..], descriptor[..12], payload), descriptor[12..]))
                    throw new InvalidDataException("Virtual Mesh part checksum mismatch.");
                var bytes = AdmitPatch(payload);
                if (bytes > maximumBytes - owned) throw new InvalidDataException("Virtual Mesh exceeds its owned-array budget.");
                var patch = MeshPatchAsset.Decode(payload, cancellationToken, length);
                if (patch.Build.SourceTriangleCount <= 0 || covered + patch.Build.SourceTriangleCount > triangles
                    || patch.Settings != settings || patch.Build.Tree.VertexCount > vertices || (patch.Build.Tree.VertexCount != 0 &&
                        (patch.Build.Tree.HasBakeUvs != (flags != 0) || math.any(patch.Build.Tree.Bounds.Min < bounds.Min)
                        || math.any(patch.Build.Tree.Bounds.Max > bounds.Max))))
                    throw new InvalidDataException("Virtual Mesh part differs from its source summary.");
                covered += patch.Build.SourceTriangleCount;
                owned += bytes;
                parts[i] = new(slot, first, patch);
            }
            finally { ArrayPool<byte>.Shared.Return(rented); }
        }
        if (stream.ReadByte() != -1) throw new InvalidDataException("Trailing virtual Mesh data.");
        try { return new(sourceHash, settings, bounds, vertices, triangles, flags != 0, parts); }
        catch (ArgumentException exception) { throw new InvalidDataException("Invalid virtual Mesh product.", exception); }
    }

    private byte[] CreateHeader()
    {
        var header = new byte[HeaderBytes];
        using (var stream = new MemoryStream(header)) {
            using var writer = new BinaryWriter(stream);
            writer.Write("SIAMSHV1"u8); writer.Write((byte)(HasBakeUvs ? 1 : 0));
            writer.Write(Settings.MaxLeafTriangles); writer.Write(Settings.MaxChildren);
            writer.Write(Settings.ParentTriangleRatio); writer.Write(Settings.NormalWeight); writer.Write(Settings.UVWeight);
            writer.Write(SourceVertexCount); writer.Write(SourceTriangleCount); writer.Write(_parts.Length);
            writer.Write(Bounds.Min.x); writer.Write(Bounds.Min.y); writer.Write(Bounds.Min.z);
            writer.Write(Bounds.Max.x); writer.Write(Bounds.Max.y); writer.Write(Bounds.Max.z);
            writer.Write(Convert.FromHexString(SourceHash)); writer.Write(Convert.FromHexString(BuildKey));
        }
        SHA256.HashData(header.AsSpan(0, 129)).CopyTo(header, 129);
        return header;
    }

    private static byte[] PartHash(ReadOnlySpan<byte> headerHash, ReadOnlySpan<byte> descriptor, ReadOnlySpan<byte> payload)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("SIAMESHVIRTUALPART1"u8);
        hash.AppendData(headerHash); hash.AppendData(descriptor); hash.AppendData(payload);
        return hash.GetHashAndReset();
    }

    private static ulong AdmitPatch(ReadOnlySpan<byte> payload)
    {
        if (!payload[..8].SequenceEqual("SIAPATC2"u8)) throw new InvalidDataException("Virtual Mesh requires raw Patch geometry.");
        Span<int> counts = stackalloc int[7];
        for (var i = 0; i < counts.Length; i++) {
            counts[i] = Int(payload, 152 + i * 16 + 8);
            if (counts[i] < 0) throw new InvalidDataException("Invalid virtual Patch array count.");
        }
        var stride = Int(payload, 180);
        if (stride is not (48 or 56)) throw new InvalidDataException("Invalid virtual Patch vertex layout.");
        return MeshPatchTree.CalculatePayloadBytes(counts, stride == 56);
    }

    private static int Int(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadInt32LittleEndian(bytes[offset..]);
    private static float Float(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadSingleLittleEndian(bytes[offset..]);
}
