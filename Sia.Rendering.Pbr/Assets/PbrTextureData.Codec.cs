using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Sia.WebGPU;

namespace Sia.Engine.Rendering.Pbr;

public sealed partial class PbrTextureData
{
    private const int HeaderBytes = 64;
    private string? _encodedHash;
    public ulong EncodedBytes => HeaderBytes + PayloadBytes;

    /// <summary>Writes one independently readable RGBA8 mip chain to a borrowed stream.</summary>
    public void Save(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!ValidShape(Width, Height, MipLevels.Length, Sampler)) throw new InvalidDataException("Invalid texture shape or sampler.");
        for (var i = 0; i < MipLevels.Length; i++)
            if (MipLevels.Span[i].Length != (long)System.Math.Max(1u, Width >> i) * System.Math.Max(1u, Height >> i) * 4)
                throw new InvalidDataException("Invalid texture mip length.");
        Span<byte> header = stackalloc byte[HeaderBytes];
        "SIATEXR1"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], Width);
        BinaryPrimitives.WriteUInt32LittleEndian(header[12..], Height);
        header[16] = Srgb ? (byte)1 : (byte)0; header[17] = (byte)MipLevels.Length;
        header[18] = (byte)Sampler.AddressU; header[19] = (byte)Sampler.AddressV;
        header[20] = (byte)Sampler.MinFilter; header[21] = (byte)Sampler.MagFilter;
        header[22] = (byte)Sampler.MipFilter; header[23] = Sampler.UseMipmaps ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt64LittleEndian(header[24..], PayloadBytes);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(header[..32]);
        foreach (var level in MipLevels.Span) hash.AppendData(level.Span);
        hash.GetHashAndReset().CopyTo(header[32..]);
        stream.Write(header);
        foreach (var level in MipLevels.Span) stream.Write(level.Span);
    }

    /// <summary>Reads one bounded file, adopts decoded arrays and leaves the borrowed stream open.</summary>
    public static PbrTextureData Load(Stream stream, ulong maximumBytes = 128ul * 1024 * 1024)
        => Load(stream, maximumBytes, default);

    internal static PbrTextureData Load(Stream stream, ulong maximumBytes, ReadOnlySpan<byte> signature)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (maximumBytes < HeaderBytes) throw new InvalidDataException("Texture header exceeds the decode limit.");
        try {
            Span<byte> header = stackalloc byte[HeaderBytes];
            if (signature.IsEmpty) stream.ReadExactly(header);
            else {
                if (signature.Length != 8) throw new ArgumentException("Texture signature must contain eight bytes.", nameof(signature));
                signature.CopyTo(header); stream.ReadExactly(header[8..]);
            }
            var (width, height, count, sampler) = ReadHeader(header, maximumBytes);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); hash.AppendData(header[..32]);
            using var encodedHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); encodedHash.AppendData(header);
            var levels = new ReadOnlyMemory<byte>[count];
            for (var i = 0; i < count; i++) {
                var length = checked((int)(System.Math.Max(1u, width >> i) * System.Math.Max(1u, height >> i) * 4));
                var bytes = GC.AllocateUninitializedArray<byte>(length); stream.ReadExactly(bytes); hash.AppendData(bytes);
                encodedHash.AppendData(bytes); levels[i] = bytes;
            }
            if (!CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), header[32..]))
                throw new InvalidDataException("Texture checksum mismatch.");
            if (stream.ReadByte() != -1) throw new InvalidDataException("Trailing texture data.");
            var result = FromValidatedMipChain(width, height, header[16] != 0, sampler, levels);
            result._encodedHash = Convert.ToHexString(encodedHash.GetHashAndReset());
            return result;
        }
        catch (EndOfStreamException error) { throw new InvalidDataException("Truncated texture file.", error); }
    }

    internal static (uint Width, uint Height, int Count, PbrTextureSampler Sampler) ReadHeader(ReadOnlySpan<byte> header, ulong maximumBytes)
    {
        if (!header[..8].SequenceEqual("SIATEXR1"u8) || header[16] > 1 || header[23] > 1)
            throw new InvalidDataException("Invalid texture signature or boolean encoding.");
        var width = BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);
        var height = BinaryPrimitives.ReadUInt32LittleEndian(header[12..]);
        var count = (int)header[17];
        var sampler = new PbrTextureSampler((WGPUAddressMode)header[18], (WGPUAddressMode)header[19],
            (WGPUFilterMode)header[20], (WGPUFilterMode)header[21], (WGPUMipmapFilterMode)header[22], header[23] != 0);
        if (!ValidShape(width, height, count, sampler)) throw new InvalidDataException("Invalid texture shape or sampler.");
        ulong payload = 0;
        for (var i = 0; i < count; i++) payload += (ulong)System.Math.Max(1u, width >> i) * System.Math.Max(1u, height >> i) * 4;
        var owned = payload + (ulong)count * (ulong)Unsafe.SizeOf<ReadOnlyMemory<byte>>();
        if (payload != BinaryPrimitives.ReadUInt64LittleEndian(header[24..]) || payload + HeaderBytes > maximumBytes || owned > maximumBytes)
            throw new InvalidDataException("Texture payload exceeds its declared shape or decode limit.");
        return (width, height, count, sampler);
    }

    internal string EncodedHash
    {
        get {
            if (_encodedHash is not null) return _encodedHash;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            Save(new HashOutput(hash));
            return _encodedHash = Convert.ToHexString(hash.GetHashAndReset());
        }
    }

    private sealed class HashOutput(IncrementalHash hash) : Stream
    {
        public override void Write(ReadOnlySpan<byte> bytes) => hash.AppendData(bytes);
        public override void Write(byte[] bytes, int offset, int count) => Write(bytes.AsSpan(offset, count));
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] bytes, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    internal static async ValueTask<PbrTextureData> LoadAsync(Stream stream, ulong maximumBytes,
        ReadOnlyMemory<byte> signature, CancellationToken token)
    {
        if (maximumBytes < HeaderBytes) throw new InvalidDataException("Texture header exceeds the decode limit.");
        try {
            var header = new byte[HeaderBytes]; signature.CopyTo(header);
            await stream.ReadExactlyAsync(header.AsMemory(signature.Length), token).ConfigureAwait(false);
            var (width, height, count, sampler) = ReadHeader(header, maximumBytes);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); hash.AppendData(header.AsSpan(0, 32));
            using var encodedHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); encodedHash.AppendData(header);
            var levels = new ReadOnlyMemory<byte>[count];
            for (var mip = 0; mip < count; mip++) {
                var length = checked((int)(System.Math.Max(1u, width >> mip) * System.Math.Max(1u, height >> mip) * 4));
                var bytes = GC.AllocateUninitializedArray<byte>(length);
                for (var offset = 0; offset < length;) {
                    var read = await stream.ReadAsync(bytes.AsMemory(offset, System.Math.Min(65536, length - offset)), token).ConfigureAwait(false);
                    if (read == 0) throw new EndOfStreamException();
                    hash.AppendData(bytes.AsSpan(offset, read)); encodedHash.AppendData(bytes.AsSpan(offset, read));
                    offset += read;
                    await Task.Yield();
                }
                levels[mip] = bytes;
            }
            if (!CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), header.AsSpan(32)))
                throw new InvalidDataException("Texture checksum mismatch.");
            if (await stream.ReadAsync(new byte[1], token).ConfigureAwait(false) != 0)
                throw new InvalidDataException("Trailing texture data.");
            var result = FromValidatedMipChain(width, height, header[16] != 0, sampler, levels);
            result._encodedHash = Convert.ToHexString(encodedHash.GetHashAndReset());
            return result;
        }
        catch (EndOfStreamException error) { throw new InvalidDataException("Truncated texture file.", error); }
    }
}
