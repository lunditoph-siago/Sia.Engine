using System.Buffers.Binary;
using System.Buffers;
using System.Security.Cryptography;
using Sia.Math;

namespace Sia.Engine.Rendering.Pbr;

public sealed partial class PbrLightmapPageData
{
    /// <summary>Writes one checksummed page without closing or seeking the caller-owned stream.</summary>
    public void Save(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _ = Write(stream, cancellationToken);
    }

    private string Write(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream); cancellationToken.ThrowIfCancellationRequested();
        Span<byte> header = stackalloc byte[60]; "SIALMPG1"u8.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], Edge);
        for (var component = 0; component < 4; component++)
            for (var channel = 0; channel < 3; channel++)
                BinaryPrimitives.WriteSingleLittleEndian(header[(12 + component * 12 + channel * 4)..], _scales[component][channel]);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(header); stream.Write(header);
        hash.AppendData(_coverage); stream.Write(_coverage);
        var scratch = ArrayPool<byte>.Shared.Rent(32768);
        try {
            for (var offset = 0; offset < _coefficients.Length;) {
                cancellationToken.ThrowIfCancellationRequested();
                var count = System.Math.Min(16384, _coefficients.Length - offset);
                var bytes = scratch.AsSpan(0, count * 2);
                for (var at = 0; at < count; at++)
                    BinaryPrimitives.WriteInt16LittleEndian(bytes[(at * 2)..], _coefficients[offset + at]);
                hash.AppendData(bytes); stream.Write(bytes); offset += count;
            }
            cancellationToken.ThrowIfCancellationRequested();
            Span<byte> checksum = stackalloc byte[32]; hash.GetCurrentHash(checksum); stream.Write(checksum);
            hash.AppendData(checksum); return Convert.ToHexString(hash.GetHashAndReset());
        }
        finally { ArrayPool<byte>.Shared.Return(scratch); }
    }

    /// <summary>Admits shape and bytes before allocating; validates integrity, exact EOF and page semantics.</summary>
    public static PbrLightmapPageData Load(Stream stream, ulong maximumBytes = MaximumEncodedBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream); cancellationToken.ThrowIfCancellationRequested();
        if (maximumBytes < 92) throw new InvalidDataException("Lightmap header exceeds its decode budget.");
        try {
            Span<byte> header = stackalloc byte[60]; stream.ReadExactly(header);
            if (!header[..8].SequenceEqual("SIALMPG1"u8)) throw new InvalidDataException("Invalid lightmap page signature.");
            var edge = BinaryPrimitives.ReadInt32LittleEndian(header[8..]); Admit(edge, maximumBytes);
            var count = edge * edge;
            if (stream.CanSeek && stream.Length - stream.Position != 32L + count * 25L)
                throw new InvalidDataException("Lightmap page length differs from its declared shape.");
            var scales = new float3[4];
            for (var component = 0; component < 4; component++)
                for (var channel = 0; channel < 3; channel++) {
                    var scale = BinaryPrimitives.ReadSingleLittleEndian(header[(12 + component * 12 + channel * 4)..]);
                    if (!float.IsFinite(scale) || scale < 0 || scale > MaximumCoefficient / short.MaxValue * 1.000001f)
                        throw new InvalidDataException("Invalid lightmap scale.");
                    scales[component][channel] = scale;
                }
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); hash.AppendData(header);
            cancellationToken.ThrowIfCancellationRequested();
            var coverage = new byte[count]; stream.ReadExactly(coverage); hash.AppendData(coverage);
            if (coverage.AsSpan().ContainsAnyExcept((byte)0, (byte)1)) throw new InvalidDataException("Invalid lightmap coverage.");
            var coefficients = new short[count * 12]; var scratch = ArrayPool<byte>.Shared.Rent(32768);
            try {
                for (var offset = 0; offset < coefficients.Length;) {
                    cancellationToken.ThrowIfCancellationRequested();
                    var length = System.Math.Min(16384, coefficients.Length - offset); var bytes = scratch.AsSpan(0, length * 2);
                    stream.ReadExactly(bytes); hash.AppendData(bytes);
                    for (var at = 0; at < length; at++)
                        coefficients[offset + at] = BinaryPrimitives.ReadInt16LittleEndian(bytes[(at * 2)..]);
                    offset += length;
                }
                cancellationToken.ThrowIfCancellationRequested();
                Span<byte> expected = stackalloc byte[32]; Span<byte> actual = stackalloc byte[32];
                stream.ReadExactly(expected); hash.GetCurrentHash(actual);
                if (!CryptographicOperations.FixedTimeEquals(expected, actual)) throw new InvalidDataException("Lightmap page checksum mismatch.");
                if (stream.ReadByte() != -1) throw new InvalidDataException("Trailing lightmap page data.");
                cancellationToken.ThrowIfCancellationRequested();
                hash.AppendData(expected);
                return new(edge, scales, coefficients, coverage, Convert.ToHexString(hash.GetHashAndReset()), cancellationToken);
            }
            finally { ArrayPool<byte>.Shared.Return(scratch); }
        }
        catch (EndOfStreamException error) { throw new InvalidDataException("Truncated lightmap page.", error); }
        catch (ArgumentException error) { throw new InvalidDataException("Invalid lightmap page.", error); }
    }
}
