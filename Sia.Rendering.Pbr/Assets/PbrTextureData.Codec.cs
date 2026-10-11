using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Sia.WebGPU;

namespace Sia.Engine.Rendering.Pbr;

public sealed partial class PbrTextureData
{
    private const int HeaderBytes = 64;
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
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (maximumBytes < HeaderBytes) throw new InvalidDataException("Texture header exceeds the decode limit.");
        try {
            Span<byte> header = stackalloc byte[HeaderBytes]; stream.ReadExactly(header);
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
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); hash.AppendData(header[..32]);
            var levels = new ReadOnlyMemory<byte>[count];
            for (var i = 0; i < count; i++) {
                var length = checked((int)(System.Math.Max(1u, width >> i) * System.Math.Max(1u, height >> i) * 4));
                var bytes = GC.AllocateUninitializedArray<byte>(length); stream.ReadExactly(bytes); hash.AppendData(bytes);
                levels[i] = bytes;
            }
            if (!CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), header[32..]))
                throw new InvalidDataException("Texture checksum mismatch.");
            if (stream.ReadByte() != -1) throw new InvalidDataException("Trailing texture data.");
            return FromValidatedMipChain(width, height, header[16] != 0, sampler, levels);
        }
        catch (EndOfStreamException error) { throw new InvalidDataException("Truncated texture file.", error); }
    }
}
