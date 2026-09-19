using System.Buffers.Binary;
using System.IO.Compression;
using Sia.Asset;

namespace Sia.Engine.Rendering;

// Content hashes cover the compressed envelope; decoded allocation is independently bounded.
public static class SceneStreamBlock
{
    public static byte[] Encode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is < 1 or > AssetChunk.MaximumLength) throw new ArgumentOutOfRangeException(nameof(bytes));
        using var output = new MemoryStream(); output.Write(new byte[16]);
        using (var compressor = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true)) compressor.Write(bytes);
        var encoded = output.ToArray();
        if (encoded.Length > AssetChunk.MaximumLength) throw new ArgumentException("Compressed chunk exceeds its transport limit.");
        "SIAGZIP1"u8.CopyTo(encoded); BinaryPrimitives.WriteInt32LittleEndian(encoded.AsSpan(8), bytes.Length);
        BinaryPrimitives.WriteInt32LittleEndian(encoded.AsSpan(12), encoded.Length - 16);
        return encoded;
    }

    public static byte[] Decode(ReadOnlySpan<byte> bytes, int maximumLength = AssetChunk.MaximumLength)
    {
        if (bytes.Length < 34 || bytes.Length > AssetChunk.MaximumLength || !bytes[..8].SequenceEqual("SIAGZIP1"u8)
            || BinaryPrimitives.ReadInt32LittleEndian(bytes[12..]) != bytes.Length - 16)
            throw new InvalidDataException("Invalid compressed scene block.");
        var length = BinaryPrimitives.ReadInt32LittleEndian(bytes[8..]);
        if (length <= 0 || length > maximumLength || length > AssetChunk.MaximumLength) throw new InvalidDataException("Decoded scene block exceeds its limit.");
        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes[^4..]) != (uint)length)
            throw new InvalidDataException("Invalid scene block size trailer.");
        var output = new byte[length];
        using var input = new MemoryStream(bytes[16..].ToArray(), false);
        using var decoder = new GZipStream(input, CompressionMode.Decompress);
        try { decoder.ReadExactly(output); }
        catch (EndOfStreamException error) { throw new InvalidDataException("Truncated scene block.", error); }
        if (decoder.ReadByte() != -1) throw new InvalidDataException("Incorrect decoded scene block length.");
        return output;
    }
}
