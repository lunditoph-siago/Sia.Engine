using System.Buffers.Binary;
using System.Buffers;
using System.IO.Compression;

namespace Sia.Engine.Mesh;

public sealed partial class MeshPatchAsset
{
    public byte[] EncodeCompressed(CancellationToken cancellationToken = default)
    {
        var raw = Shuffle(Encode(cancellationToken), restore: false, cancellationToken);
        using var output = new MemoryStream();
        output.Write(new byte[24]);
        using (var compressor = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true)) {
            for (var offset = 0; offset < raw.Length; offset += 65536) {
                cancellationToken.ThrowIfCancellationRequested();
                compressor.Write(raw.AsSpan(offset, System.Math.Min(65536, raw.Length - offset)));
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        var bytes = output.ToArray();
        "SIAGZIP1"u8.CopyTo(bytes);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(8), raw.Length);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(16), bytes.Length);
        return bytes;
    }

    private static unsafe byte[] Decompress(ReadOnlySpan<byte> bytes, int maximumDecodedBytes, CancellationToken cancellationToken)
    {
        Require(bytes.Length > 24, "Truncated compressed patch asset.");
        var length = BinaryPrimitives.ReadInt64LittleEndian(bytes[8..]);
        Require(length >= HeaderSize && length <= maximumDecodedBytes, "Invalid compressed patch length or decoded byte limit exceeded.");
        Require(BinaryPrimitives.ReadInt64LittleEndian(bytes[16..]) == bytes.Length, "Compressed patch length mismatch or trailing data.");
        var raw = ArrayPool<byte>.Shared.Rent((int)length);
        try {
            fixed (byte* pointer = bytes) {
                using var input = new UnmanagedMemoryStream(pointer + 24, bytes.Length - 24);
                using var decoder = new GZipStream(input, CompressionMode.Decompress);
                for (var offset = 0; offset < length; offset += 65536) {
                    cancellationToken.ThrowIfCancellationRequested();
                    decoder.ReadExactly(raw.AsSpan(offset, (int)System.Math.Min(65536, length - offset)));
                }
                Require(decoder.ReadByte() == -1, "Compressed patch exceeds the declared decoded length.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            return Shuffle(raw.AsSpan(0, (int)length), restore: true, cancellationToken);
        }
        catch (EndOfStreamException error) { throw new InvalidDataException("Truncated compressed patch payload.", error); }
        finally { ArrayPool<byte>.Shared.Return(raw); }
    }

    private static byte[] Shuffle(ReadOnlySpan<byte> bytes, bool restore, CancellationToken cancellationToken)
    {
        Require(bytes.Length >= HeaderSize, "Truncated patch sections.");
        var version = BinaryPrimitives.ReadInt32LittleEndian(bytes[8..]);
        Require(version is 1 or FormatVersion, "Unsupported patch asset format version.");
        var output = new byte[bytes.Length];
        bytes[..HeaderSize].CopyTo(output);
        var offset = HeaderSize;
        for (var section = 0; section < Strides.Length; section++) {
            cancellationToken.ThrowIfCancellationRequested();
            var descriptor = bytes[(160 + section * 16)..];
            var count = BinaryPrimitives.ReadInt32LittleEndian(descriptor[8..]);
            var stride = SectionStride(version, section);
            Require(BinaryPrimitives.ReadInt64LittleEndian(descriptor) == offset && count >= 0
                && BinaryPrimitives.ReadInt32LittleEndian(descriptor[12..]) == stride
                && (long)count * stride <= bytes.Length - offset, "Invalid compressed patch section.");
            for (var lane = 0; lane < stride; lane++) {
                for (var i = 0; i < count; i++) {
                    if ((i & 65535) == 0) { cancellationToken.ThrowIfCancellationRequested(); }
                    var interleaved = offset + i * stride + lane;
                    var planar = offset + lane * count + i;
                    output[restore ? interleaved : planar] = bytes[restore ? planar : interleaved];
                }
            }
            offset += count * stride;
        }
        Require(offset == bytes.Length, "Unexpected compressed patch section data.");
        return output;
    }
}
