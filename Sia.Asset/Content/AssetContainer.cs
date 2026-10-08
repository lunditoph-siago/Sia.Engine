using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;

namespace Sia.Asset;

internal enum AssetEncoding : byte { Raw, Brotli }

// The unified AssetLibrary owns IO and admission. Domain codecs receive verified payloads only.
internal static class AssetContainer
{
    public const int HeaderBytes = 64;
    private static ReadOnlySpan<byte> Magic => "SIAASSET"u8;

    internal static byte[] Encode(ushort kind, ReadOnlySpan<byte> payload, AssetEncoding encoding = AssetEncoding.Raw)
    {
        if (kind == 0) throw new ArgumentOutOfRangeException(nameof(kind));
        byte[] encoded;
        switch (encoding) {
            case AssetEncoding.Raw:
                encoded = payload.ToArray();
                break;
            case AssetEncoding.Brotli:
                encoded = new byte[BrotliEncoder.GetMaxCompressedLength(payload.Length)];
                if (!BrotliEncoder.TryCompress(payload, encoded, out var written, quality: 5, window: 22))
                    throw new InvalidOperationException("Asset compression failed.");
                Array.Resize(ref encoded, written);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(encoding));
        }
        var container = new byte[checked(HeaderBytes + encoded.Length)];
        Magic.CopyTo(container);
        BinaryPrimitives.WriteUInt16LittleEndian(container.AsSpan(8), kind);
        container[10] = (byte)encoding;
        container[11] = 1;
        BinaryPrimitives.WriteUInt64LittleEndian(container.AsSpan(16), (ulong)encoded.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(container.AsSpan(24), (ulong)payload.Length);
        encoded.CopyTo(container, HeaderBytes);
        ComputeId(container.AsSpan(0, 32), encoded).WriteBytes(container.AsSpan(32));
        return container;
    }

    internal static AssetReference<T> GetReference<T>(ReadOnlySpan<byte> container, ushort kind)
    {
        var header = ReadHeader(container, kind);
        if (container.Length - HeaderBytes != header.Encoded)
            throw new InvalidDataException("Asset encoded length does not match the container.");
        if (ComputeId(container[..32], container[HeaderBytes..]) != header.Id)
            throw new InvalidDataException("Asset checksum mismatch.");
        return new(header.Id, header.Encoded, header.Decoded);
    }

    internal static async ValueTask<byte[]> ReadAsync<T>(Stream input, AssetReference<T> reference,
        ushort kind, long maximumEncodedBytes, long maximumDecodedBytes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumEncodedBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumDecodedBytes);
        if (kind == 0) throw new ArgumentOutOfRangeException(nameof(kind));
        if (reference.Id == default) throw new ArgumentException("An initialized asset reference is required.", nameof(reference));
        if (reference.EncodedBytes > maximumEncodedBytes || reference.DecodedBytes > maximumDecodedBytes)
            throw new InvalidDataException("Asset reference exceeds its admitted bounds.");
        cancellationToken.ThrowIfCancellationRequested();
        var prefix = new byte[HeaderBytes];
        await input.ReadExactlyAsync(prefix, cancellationToken).ConfigureAwait(false);
        var header = ReadHeader(prefix, kind);
        if (header.Id != reference.Id || header.Encoded != reference.EncodedBytes || header.Decoded != reference.DecodedBytes)
            throw new InvalidDataException("Asset header does not match its typed reference.");
        var encoded = new byte[header.Encoded];
        await input.ReadExactlyAsync(encoded, cancellationToken).ConfigureAwait(false);
        var trailing = new byte[1];
        if (await input.ReadAsync(trailing, cancellationToken).ConfigureAwait(false) != 0)
            throw new InvalidDataException("Trailing asset data.");
        if (ComputeId(prefix.AsSpan(0, 32), encoded) != header.Id)
            throw new InvalidDataException("Asset checksum mismatch.");
        cancellationToken.ThrowIfCancellationRequested();
        if (header.Encoding == AssetEncoding.Raw) return encoded;
        // Brotli needs destination space to consume an empty stream's terminal marker.
        // The single-byte sentinel also detects any output contrary to a declared zero length.
        var decoded = new byte[Math.Max(1, header.Decoded)];
        using var decoder = new BrotliDecoder();
        var status = decoder.Decompress(encoded, decoded, out var consumed, out var produced);
        if (status != OperationStatus.Done || consumed != encoded.Length || produced != header.Decoded)
            throw new InvalidDataException("Asset compression lengths do not match the complete payload.");
        cancellationToken.ThrowIfCancellationRequested();
        return header.Decoded == 0 ? [] : decoded;
    }

    private static (AssetId Id, int Encoded, int Decoded, AssetEncoding Encoding) ReadHeader(ReadOnlySpan<byte> prefix, ushort kind)
    {
        if (prefix.Length < HeaderBytes) throw new InvalidDataException("Truncated asset header.");
        if (!prefix[..8].SequenceEqual(Magic) || BinaryPrimitives.ReadUInt16LittleEndian(prefix[8..]) != kind
            || kind == 0 || prefix[11] != 1 || BinaryPrimitives.ReadUInt32LittleEndian(prefix[12..]) != 0)
            throw new InvalidDataException("Invalid current asset header.");
        var encoding = (AssetEncoding)prefix[10];
        if (encoding is not (AssetEncoding.Raw or AssetEncoding.Brotli))
            throw new InvalidDataException("Unknown asset encoding.");
        var encoded = BinaryPrimitives.ReadUInt64LittleEndian(prefix[16..]);
        var decoded = BinaryPrimitives.ReadUInt64LittleEndian(prefix[24..]);
        if (encoded > int.MaxValue || decoded > int.MaxValue || encoding == AssetEncoding.Raw && encoded != decoded)
            throw new InvalidDataException("Invalid bounded asset lengths.");
        return (new(prefix.Slice(32, AssetId.ByteCount)), (int)encoded, (int)decoded, encoding);
    }

    private static AssetId ComputeId(ReadOnlySpan<byte> prefix, ReadOnlySpan<byte> encoded)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(prefix);
        hash.AppendData(encoded);
        Span<byte> digest = stackalloc byte[AssetId.ByteCount];
        hash.GetHashAndReset(digest);
        return new(digest);
    }
}
