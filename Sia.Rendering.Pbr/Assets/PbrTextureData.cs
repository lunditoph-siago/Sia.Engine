using Sia.WebGPU;

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

public sealed partial class PbrTextureData
{
    public uint Width { get; }
    public uint Height { get; }
    public bool Srgb { get; }
    public PbrTextureSampler Sampler { get; }
    public ReadOnlyMemory<ReadOnlyMemory<byte>> MipLevels { get; }
    public ulong PayloadBytes { get; }

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

    /// <summary>Shares immutable pixels in a contiguous mip range; only the small level table is copied.</summary>
    public PbrTextureData SliceMipChain(int firstMip, int? levelCount = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(firstMip);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(firstMip, MipLevels.Length);
        var count = levelCount ?? MipLevels.Length - firstMip;
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, MipLevels.Length - firstMip);
        return new(System.Math.Max(1u, Width >> firstMip), System.Math.Max(1u, Height >> firstMip),
            Srgb, Sampler, MipLevels.Span.Slice(firstMip, count).ToArray()) {
                _sourceLevel0Hash = _sourceLevel0Hash ?? _level0Hash,
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

    internal static bool ValidShape(uint width, uint height, int levels, PbrTextureSampler sampler)
        => width is > 0 and <= 8192 && height is > 0 and <= 8192 && levels is > 0 and <= 14
            && (levels == 1 || (width >> (levels - 2)) > 1 || (height >> (levels - 2)) > 1) && sampler.IsValid;
}
