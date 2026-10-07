using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;

namespace Sia.Asset;

/// <summary>Composes a typed payload codec with bounded IO, SHA256 and exact EOF validation.</summary>
public static class ChecksummedAsset
{
    /// <summary>Leaves the borrowed source open. No seeking or Length support is required.</summary>
    public static T Read<T, TCodec>(Stream source) where TCodec : IChecksummedAssetCodec<T>
        => Read<T, TCodec>(source, int.MaxValue);

    /// <summary>Rejects a declared payload above the caller's bound before reading or allocating its body.</summary>
    public static T Read<T, TCodec>(Stream source, int maximumPayloadBytes) where TCodec : IChecksummedAssetCodec<T>
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumPayloadBytes);
        var headerBytes = TCodec.HeaderBytes;
        ArgumentOutOfRangeException.ThrowIfNegative(headerBytes);
        var header = new byte[headerBytes];
        source.ReadExactly(header);
        var size = PayloadSize<T, TCodec>(header, maximumPayloadBytes);
        var bytes = new byte[size];
        header.CopyTo(bytes, 0);
        source.ReadExactly(bytes.AsSpan(headerBytes));
        Span<byte> checksum = stackalloc byte[32];
        source.ReadExactly(checksum);
        Span<byte> expected = stackalloc byte[32];
        SHA256.HashData(bytes, expected);
        if (!CryptographicOperations.FixedTimeEquals(checksum, expected) || source.ReadByte() != -1)
            throw new InvalidDataException("Asset checksum mismatch or trailing data.");
        using var payload = new MemoryStream(bytes, writable: false);
        return ReadPayload<T, TCodec>(payload);
    }

    /// <summary>Verifies an existing encoded buffer without copying an array-backed payload.</summary>
    public static T Read<T, TCodec>(ReadOnlyMemory<byte> source, int maximumPayloadBytes) where TCodec : IChecksummedAssetCodec<T>
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumPayloadBytes);
        var headerBytes = TCodec.HeaderBytes;
        ArgumentOutOfRangeException.ThrowIfNegative(headerBytes);
        if (source.Length < headerBytes || source.Length - headerBytes < 32)
            throw new EndOfStreamException("Asset prefix or checksum is incomplete.");
        var size = PayloadSize<T, TCodec>(source.Span[..headerBytes], maximumPayloadBytes);
        if ((long)source.Length != (long)size + 32)
            throw new InvalidDataException("Asset size mismatch or trailing data.");
        Span<byte> expected = stackalloc byte[32];
        SHA256.HashData(source.Span[..size], expected);
        if (!CryptographicOperations.FixedTimeEquals(source.Span[size..], expected))
            throw new InvalidDataException("Asset checksum mismatch.");
        var body = source[..size];
        using var payload = MemoryMarshal.TryGetArray(body, out var segment)
            ? new MemoryStream(segment.Array!, segment.Offset, segment.Count, writable: false)
            : new MemoryStream(body.ToArray(), writable: false);
        return ReadPayload<T, TCodec>(payload);
    }

    private static int PayloadSize<T, TCodec>(ReadOnlySpan<byte> header, int maximumPayloadBytes)
        where TCodec : IChecksummedAssetCodec<T>
    {
        var size = TCodec.GetReadSize(header);
        if (size < header.Length) throw new InvalidDataException("Asset payload size is smaller than its prefix.");
        if (size > maximumPayloadBytes) throw new InvalidDataException("Asset payload exceeds its configured size limit.");
        return size;
    }

    private static T ReadPayload<T, TCodec>(MemoryStream payload) where TCodec : IChecksummedAssetCodec<T>
    {
        using var reader = new BinaryReader(payload, Encoding.UTF8, leaveOpen: true);
        var value = TCodec.ReadPayload(reader);
        if (payload.Position != payload.Length)
            throw new InvalidDataException("Asset codec did not consume the complete payload.");
        return value;
    }

    /// <summary>Encodes and validates the payload size before writing; leaves the borrowed destination open.</summary>
    public static void Write<T, TCodec>(Stream destination, T value) where TCodec : IChecksummedAssetCodec<T>
    {
        ArgumentNullException.ThrowIfNull(destination);
        var size = TCodec.GetWriteSize(value);
        ArgumentOutOfRangeException.ThrowIfNegative(size);
        var bytes = new byte[size];
        // A fixed backing array prevents a codec from growing beyond its declared bound.
        using var payload = new MemoryStream(bytes, writable: true);
        using (var writer = new BinaryWriter(payload, Encoding.UTF8, leaveOpen: true))
            TCodec.WritePayload(writer, value);
        if (payload.Position != size)
            throw new InvalidDataException("Asset codec did not write its declared payload size.");
        Span<byte> checksum = stackalloc byte[32];
        SHA256.HashData(bytes, checksum);
        destination.Write(bytes);
        destination.Write(checksum);
    }
}
