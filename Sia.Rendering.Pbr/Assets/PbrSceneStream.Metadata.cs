using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
using Sia.Asset;

namespace Sia.Engine.Rendering.Pbr;

public sealed partial class PbrSceneStream
{
    // Hierarchy metadata is separate from the unchanged 8 MiB chunk manifest.
    public const int MaximumMetadataBytes = 32 * 1024 * 1024;
    private const int k_MaximumDecodedMetadata = 128 * 1024 * 1024;

    private static byte[] EncodeMetadata(byte[] json)
    {
        if (json.Length > k_MaximumDecodedMetadata)
            throw new ArgumentException("Decoded hierarchy metadata exceeds its budget.");
        if (json.Length <= AssetChunkManifest.MaximumBytes) return json;
        using var output = new MemoryStream();
        output.Write(new byte[16]);
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, true)) gzip.Write(json);
        var bytes = output.ToArray();
        if (bytes.Length > MaximumMetadataBytes)
            throw new ArgumentException($"Compressed hierarchy metadata requires {bytes.Length} bytes, exceeding {MaximumMetadataBytes}.");
        "SIAMETA\0"u8.CopyTo(bytes);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), json.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12), bytes.Length - 16);
        return bytes;
    }

    private static ReadOnlyMemory<byte> DecodeMetadata(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length < 8 || !bytes.Span[..8].SequenceEqual("SIAMETA\0"u8)) return bytes;
        if (bytes.Length < 34) throw new InvalidDataException("Truncated hierarchy metadata envelope.");
        var length = BinaryPrimitives.ReadInt32LittleEndian(bytes.Span[8..]);
        if (length <= 0 || length > k_MaximumDecodedMetadata
            || BinaryPrimitives.ReadInt32LittleEndian(bytes.Span[12..]) != bytes.Length - 16
            || BinaryPrimitives.ReadUInt32LittleEndian(bytes.Span[^4..]) != length)
            throw new InvalidDataException("Invalid hierarchy metadata reservation.");
        var compressed = bytes[16..];
        using var input = MemoryMarshal.TryGetArray(compressed, out var segment)
            ? new MemoryStream(segment.Array!, segment.Offset, segment.Count, false)
            : new MemoryStream(compressed.ToArray(), false);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        var output = new byte[length];
        try { gzip.ReadExactly(output); }
        catch (EndOfStreamException error) { throw new InvalidDataException("Truncated hierarchy metadata.", error); }
        if (gzip.ReadByte() != -1) throw new InvalidDataException("Hierarchy metadata length mismatch.");
        return output;
    }
}
