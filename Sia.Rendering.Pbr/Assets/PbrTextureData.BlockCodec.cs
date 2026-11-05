using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Sia.Math;
using Sia.WebGPU;

namespace Sia.Engine.Rendering.Pbr;

public sealed partial class PbrTextureData
{
    private const int BlockHeaderBytes = 160;

    private void SaveBlocks(Stream stream)
    {
        if (!ValidShape(Width, Height, MipLevels.Length, Sampler))
            throw new InvalidDataException("Invalid block texture shape or sampler.");
        _ = GpuFormat;
        for (var mip = 0; mip < MipLevels.Length; mip++)
            if ((ulong)MipLevels.Span[mip].Length != MipPayloadBytes(System.Math.Max(1u, Width >> mip), System.Math.Max(1u, Height >> mip), Format))
                throw new InvalidDataException("Invalid block texture mip length.");
        Span<byte> header = stackalloc byte[BlockHeaderBytes]; header.Clear();
        "SIATEXB\0"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], Width);
        BinaryPrimitives.WriteUInt32LittleEndian(header[12..], Height);
        header[16] = Srgb ? (byte)1 : (byte)0; header[17] = (byte)MipLevels.Length;
        header[18] = (byte)Sampler.AddressU; header[19] = (byte)Sampler.AddressV;
        header[20] = (byte)Sampler.MinFilter; header[21] = (byte)Sampler.MagFilter;
        header[22] = (byte)Sampler.MipFilter; header[23] = Sampler.UseMipmaps ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt64LittleEndian(header[24..], PayloadBytes);
        header[32] = (byte)Format;
        BinaryPrimitives.WriteSingleLittleEndian(header[36..], TransportAverage.x);
        BinaryPrimitives.WriteSingleLittleEndian(header[40..], TransportAverage.y);
        BinaryPrimitives.WriteSingleLittleEndian(header[44..], TransportAverage.z);
        Convert.FromHexString(SourceLevel0Hash).CopyTo(header[48..80]);
        Convert.FromHexString(BuildKey!).CopyTo(header[80..112]);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(header[..128]);
        foreach (var mip in MipLevels.Span) hash.AppendData(mip.Span);
        hash.GetHashAndReset().CopyTo(header[128..]);
        stream.Write(header);
        foreach (var mip in MipLevels.Span) stream.Write(mip.Span);
    }

    private static PbrTextureData LoadBlocks(Stream stream, ulong maximumBytes, ReadOnlySpan<byte> signature)
    {
        if (maximumBytes < BlockHeaderBytes) throw new InvalidDataException("Block texture header exceeds the decode limit.");
        Span<byte> header = stackalloc byte[BlockHeaderBytes]; signature.CopyTo(header);
        stream.ReadExactly(header[8..]);
        var info = ReadBlockHeader(header, maximumBytes);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); hash.AppendData(header[..128]);
        using var encodedHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); encodedHash.AppendData(header);
        var levels = new ReadOnlyMemory<byte>[info.Count];
        for (var mip = 0; mip < levels.Length; mip++) {
            var length = checked((int)MipPayloadBytes(System.Math.Max(1u, info.Width >> mip), System.Math.Max(1u, info.Height >> mip), info.Format));
            var bytes = GC.AllocateUninitializedArray<byte>(length); stream.ReadExactly(bytes);
            hash.AppendData(bytes); encodedHash.AppendData(bytes); levels[mip] = bytes;
        }
        if (!CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), header[128..]))
            throw new InvalidDataException("Block texture checksum mismatch.");
        if (stream.ReadByte() != -1) throw new InvalidDataException("Trailing block texture data.");
        var data = FromValidatedBlocks(info.Width, info.Height, header[16] != 0, info.Sampler, levels,
            info.Format, Convert.ToHexString(header[48..80]), info.Average, Convert.ToHexString(header[80..112]));
        data._encodedHash = Convert.ToHexString(encodedHash.GetHashAndReset());
        return data;
    }

    private static async ValueTask<PbrTextureData> LoadBlocksAsync(Stream stream, ulong maximumBytes,
        ReadOnlyMemory<byte> signature, CancellationToken token)
    {
        if (maximumBytes < BlockHeaderBytes) throw new InvalidDataException("Block texture header exceeds the decode limit.");
        var header = new byte[BlockHeaderBytes]; signature.CopyTo(header);
        await stream.ReadExactlyAsync(header.AsMemory(8), token).ConfigureAwait(false);
        var info = ReadBlockHeader(header, maximumBytes);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); hash.AppendData(header.AsSpan(0, 128));
        using var encodedHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); encodedHash.AppendData(header);
        var levels = new ReadOnlyMemory<byte>[info.Count];
        for (var mip = 0; mip < levels.Length; mip++) {
            var bytes = GC.AllocateUninitializedArray<byte>(checked((int)MipPayloadBytes(
                System.Math.Max(1u, info.Width >> mip), System.Math.Max(1u, info.Height >> mip), info.Format)));
            for (var offset = 0; offset < bytes.Length;) {
                var read = await stream.ReadAsync(bytes.AsMemory(offset, System.Math.Min(65536, bytes.Length - offset)), token).ConfigureAwait(false);
                if (read == 0) throw new EndOfStreamException();
                hash.AppendData(bytes.AsSpan(offset, read)); encodedHash.AppendData(bytes.AsSpan(offset, read)); offset += read;
                await Task.Yield();
            }
            levels[mip] = bytes;
        }
        if (!CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), header.AsSpan(128)))
            throw new InvalidDataException("Block texture checksum mismatch.");
        if (await stream.ReadAsync(new byte[1], token).ConfigureAwait(false) != 0)
            throw new InvalidDataException("Trailing block texture data.");
        var data = FromValidatedBlocks(info.Width, info.Height, header[16] != 0, info.Sampler, levels,
            info.Format, Convert.ToHexString(header.AsSpan(48, 32)), info.Average, Convert.ToHexString(header.AsSpan(80, 32)));
        data._encodedHash = Convert.ToHexString(encodedHash.GetHashAndReset());
        return data;
    }

    private static (uint Width, uint Height, int Count, PbrTextureSampler Sampler, PbrTextureFormat Format, float3 Average)
        ReadBlockHeader(ReadOnlySpan<byte> header, ulong maximumBytes)
    {
        var format = (PbrTextureFormat)header[32];
        if (!header[..8].SequenceEqual("SIATEXB\0"u8) || header[16] > 1 || header[23] > 1
            || format is not (PbrTextureFormat.Bc7 or PbrTextureFormat.Bc5) || format == PbrTextureFormat.Bc5 && header[16] != 0
            || header[33..36].IndexOfAnyExcept((byte)0) >= 0 || header[112..128].IndexOfAnyExcept((byte)0) >= 0)
            throw new InvalidDataException("Invalid block texture format, signature or flags.");
        var width = BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);
        var height = BinaryPrimitives.ReadUInt32LittleEndian(header[12..]);
        var count = (int)header[17];
        var sampler = new PbrTextureSampler((WGPUAddressMode)header[18], (WGPUAddressMode)header[19],
            (WGPUFilterMode)header[20], (WGPUFilterMode)header[21], (WGPUMipmapFilterMode)header[22], header[23] != 0);
        if (!ValidShape(width, height, count, sampler)) throw new InvalidDataException("Invalid block texture shape or sampler.");
        var average = new float3(BinaryPrimitives.ReadSingleLittleEndian(header[36..]),
            BinaryPrimitives.ReadSingleLittleEndian(header[40..]), BinaryPrimitives.ReadSingleLittleEndian(header[44..]));
        if (!math.all(math.isfinite(average)) || math.any(average < 0) || math.any(average > 1))
            throw new InvalidDataException("Invalid block texture transport average.");
        ulong payload = 0;
        for (var mip = 0; mip < count; mip++)
            payload = checked(payload + MipPayloadBytes(System.Math.Max(1u, width >> mip), System.Math.Max(1u, height >> mip), format));
        var owned = payload + (ulong)count * (ulong)Unsafe.SizeOf<ReadOnlyMemory<byte>>();
        if (payload != BinaryPrimitives.ReadUInt64LittleEndian(header[24..]) || payload + BlockHeaderBytes > maximumBytes || owned > maximumBytes)
            throw new InvalidDataException("Block texture payload exceeds its declared shape or decode limit.");
        return (width, height, count, sampler, format, average);
    }
}
