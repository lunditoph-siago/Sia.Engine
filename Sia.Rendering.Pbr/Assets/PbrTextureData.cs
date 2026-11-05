using Sia.WebGPU;
using Sia.Math;

namespace Sia.Engine.Rendering.Pbr;

public readonly record struct PbrTextureSampler(
    WGPUAddressMode AddressU,
    WGPUAddressMode AddressV,
    WGPUFilterMode MinFilter,
    WGPUFilterMode MagFilter,
    WGPUMipmapFilterMode MipFilter,
    bool UseMipmaps)
{
    internal bool IsValid => AddressU is WGPUAddressMode.Repeat or WGPUAddressMode.MirrorRepeat or WGPUAddressMode.ClampToEdge
        && AddressV is WGPUAddressMode.Repeat or WGPUAddressMode.MirrorRepeat or WGPUAddressMode.ClampToEdge
        && MinFilter is WGPUFilterMode.Nearest or WGPUFilterMode.Linear
        && MagFilter is WGPUFilterMode.Nearest or WGPUFilterMode.Linear
        && MipFilter is WGPUMipmapFilterMode.Nearest or WGPUMipmapFilterMode.Linear;

    public static PbrTextureSampler Default => new(WGPUAddressMode.Repeat, WGPUAddressMode.Repeat,
        WGPUFilterMode.Linear, WGPUFilterMode.Linear, WGPUMipmapFilterMode.Linear, true);
}

/// <summary>Texture storage encoding. Block formats contain GPU-ready blocks, never RGBA pixels.</summary>
public enum PbrTextureFormat : byte
{
    Rgba8 = 0,
    Bc7 = 1,
    Bc5 = 2
}

public sealed partial class PbrTextureData
{
    public uint Width { get; }
    public uint Height { get; }
    public bool Srgb { get; }
    public PbrTextureSampler Sampler { get; }
    /// <summary>Immutable per-level storage in Format: RGBA8 pixels or GPU-ready BC blocks.</summary>
    public ReadOnlyMemory<ReadOnlyMemory<byte>> MipLevels { get; }
    public ulong PayloadBytes { get; }
    public PbrTextureFormat Format { get; private init; }
    public WGPUTextureFormat GpuFormat => GpuFormatFor(Format, Srgb);
    public TextureBlockLayout BlockLayout => TextureBlockLayout.For(GpuFormat);
    public string? BuildKey { get; private init; }
    private float3? _transportAverage;

    /// <summary>Linear mean used by CPU transport; block products carry the offline raw-source value.</summary>
    public float3 TransportAverage {
        get {
            if (_transportAverage is { } average) return average;
            var bytes = MipLevels.Span[^1].Span;
            var sum = float3.zero;
            float Decode(byte value) {
                var x = value / 255f;
                return Srgb ? x <= .04045f ? x / 12.92f : MathF.Pow((x + .055f) / 1.055f, 2.4f) : x;
            }
            for (var at = 0; at < bytes.Length; at += 4)
                sum += new float3(Decode(bytes[at]), Decode(bytes[at + 1]), Decode(bytes[at + 2]));
            return (_transportAverage = sum / (bytes.Length / 4f)).Value;
        }
    }

    internal static WGPUTextureFormat GpuFormatFor(PbrTextureFormat format, bool srgb)
        => format switch {
            PbrTextureFormat.Rgba8 => srgb ? WGPUTextureFormat.RGBA8UnormSrgb : WGPUTextureFormat.RGBA8Unorm,
            PbrTextureFormat.Bc7 => srgb ? WGPUTextureFormat.BC7RGBAUnormSrgb : WGPUTextureFormat.BC7RGBAUnorm,
            PbrTextureFormat.Bc5 when !srgb => WGPUTextureFormat.BC5RGUnorm,
            _ => throw new ArgumentException("Unsupported texture storage format or color space.", nameof(format))
        };

    internal static ulong MipPayloadBytes(uint width, uint height, PbrTextureFormat format)
        => TextureBlockLayout.For(GpuFormatFor(format, false)).MipByteLength(width, height);

    internal static int CodecHeaderBytes(PbrTextureFormat format) => format == PbrTextureFormat.Rgba8 ? HeaderBytes : BlockHeaderBytes;

    private PbrTextureData(uint width, uint height, bool srgb, PbrTextureSampler sampler, ReadOnlyMemory<byte>[] levels)
    {
        Width = width;
        Height = height;
        Srgb = srgb;
        Sampler = sampler;
        MipLevels = levels;
        foreach (var level in levels) PayloadBytes += (ulong)level.Length;
    }

    internal static PbrTextureData FromValidatedMipChain(uint width, uint height, bool srgb,
        PbrTextureSampler sampler, ReadOnlyMemory<byte>[] levels) => new(width, height, srgb, sampler, levels);

    /// <summary>Shares immutable pixels or GPU blocks in a contiguous mip range; only the small level table is copied.</summary>
    public PbrTextureData SliceMipChain(int firstMip, int? levelCount = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(firstMip);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(firstMip, MipLevels.Length);
        var count = levelCount ?? MipLevels.Length - firstMip;
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, MipLevels.Length - firstMip);
        return new(System.Math.Max(1u, Width >> firstMip), System.Math.Max(1u, Height >> firstMip),
            Srgb, Sampler, MipLevels.Span.Slice(firstMip, count).ToArray()) {
                Format = Format, BuildKey = BuildKey, _transportAverage = TransportAverage,
                _sourceLevel0Hash = SourceLevel0Hash,
                _level0Hash = firstMip == 0 ? _level0Hash : null
            };
    }

    public static PbrTextureData Create(uint width, uint height, bool srgb, ReadOnlySpan<ReadOnlyMemory<byte>> levels,
        PbrTextureSampler? sampler = null)
    {
        var sampling = sampler ?? PbrTextureSampler.Default;
        if (!ValidShape(width, height, levels.Length, sampling)) {
            throw new ArgumentException("Invalid PBR texture dimensions, mip levels or sampler.");
        }
        var w = width;
        var h = height;
        for (var i = 0; i < levels.Length; i++) {
            if (levels[i].Length != (long)w * h * 4 || (i > 0 && (width >> (i - 1)) <= 1 && (height >> (i - 1)) <= 1)) {
                throw new ArgumentException("PBR textures require contiguous RGBA8 mip levels.", nameof(levels));
            }
            w = System.Math.Max(1u, w / 2);
            h = System.Math.Max(1u, h / 2);
        }
        var copied = new ReadOnlyMemory<byte>[levels.Length];
        for (var i = 0; i < copied.Length; i++) {
            copied[i] = levels[i].ToArray();
        }
        return new(width, height, srgb, sampling, copied);
    }

    /// <summary>Copies GPU-ready blocks and their canonical raw-source identity. Encoding is an offline responsibility.</summary>
    public static PbrTextureData CreateBlocks(uint width, uint height, bool srgb, PbrTextureFormat format,
        ReadOnlySpan<ReadOnlyMemory<byte>> levels, string sourceLevel0Hash, float3 transportAverage,
        string buildKey, PbrTextureSampler? sampler = null)
    {
        var sampling = sampler ?? PbrTextureSampler.Default;
        if (format is not (PbrTextureFormat.Bc7 or PbrTextureFormat.Bc5)
            || !ValidShape(width, height, levels.Length, sampling))
            throw new ArgumentException("Invalid block texture shape or format.");
        _ = GpuFormatFor(format, srgb);
        sourceLevel0Hash = RequireDigest(sourceLevel0Hash, nameof(sourceLevel0Hash));
        buildKey = RequireDigest(buildKey, nameof(buildKey));
        if (!math.all(math.isfinite(transportAverage)) || math.any(transportAverage < 0) || math.any(transportAverage > 1))
            throw new ArgumentException("Transport average must be finite and in the linear [0,1] range.", nameof(transportAverage));
        var owned = new ReadOnlyMemory<byte>[levels.Length];
        for (var mip = 0; mip < levels.Length; mip++) {
            if ((ulong)levels[mip].Length != MipPayloadBytes(System.Math.Max(1u, width >> mip), System.Math.Max(1u, height >> mip), format))
                throw new ArgumentException("Block texture mip length differs from its shape.", nameof(levels));
            owned[mip] = levels[mip].ToArray();
        }
        return FromValidatedBlocks(width, height, srgb, sampling, owned, format, sourceLevel0Hash, transportAverage, buildKey);
    }

    private static PbrTextureData FromValidatedBlocks(uint width, uint height, bool srgb, PbrTextureSampler sampler,
        ReadOnlyMemory<byte>[] levels, PbrTextureFormat format, string sourceLevel0Hash, float3 transportAverage, string buildKey)
        => new(width, height, srgb, sampler, levels) {
            Format = format, _sourceLevel0Hash = sourceLevel0Hash, _transportAverage = transportAverage, BuildKey = buildKey
        };

    private static string RequireDigest(string value, string name)
        => value is { Length: 64 } && value.All(char.IsAsciiHexDigit) ? value.ToUpperInvariant()
            : throw new ArgumentException("Expected a SHA-256 hexadecimal digest.", name);

    internal static bool ValidShape(uint width, uint height, int levels, PbrTextureSampler sampler)
        => width is > 0 and <= 8192 && height is > 0 and <= 8192 && levels is > 0 and <= 14
            && (levels == 1 || (width >> (levels - 2)) > 1 || (height >> (levels - 2)) > 1) && sampler.IsValid;
}
