using Sia.WebGPU;

namespace Sia.Engine.Rendering.Pbr;

public readonly record struct PbrTextureSampler(WGPUAddressMode AddressU, WGPUAddressMode AddressV,
    WGPUFilterMode MinFilter, WGPUFilterMode MagFilter, WGPUMipmapFilterMode MipFilter, bool UseMipmaps)
{
    public static PbrTextureSampler Default => new(WGPUAddressMode.Repeat, WGPUAddressMode.Repeat,
        WGPUFilterMode.Linear, WGPUFilterMode.Linear, WGPUMipmapFilterMode.Linear, true);
}

public sealed class PbrTextureData
{
    public uint Width { get; }
    public uint Height { get; }
    public bool Srgb { get; }
    public PbrTextureSampler Sampler { get; }
    public ReadOnlyMemory<ReadOnlyMemory<byte>> MipLevels { get; }

    private PbrTextureData(uint width, uint height, bool srgb, PbrTextureSampler sampler, ReadOnlyMemory<byte>[] levels)
    {
        Width = width; Height = height; Srgb = srgb; Sampler = sampler; MipLevels = levels;
    }

    public static PbrTextureData Create(uint width, uint height, bool srgb, ReadOnlySpan<ReadOnlyMemory<byte>> levels,
        PbrTextureSampler? sampler = null)
    {
        var sampling = sampler ?? PbrTextureSampler.Default;
        if (width == 0 || height == 0 || width > 8192 || height > 8192 || levels.IsEmpty
            || sampling.AddressU is not (WGPUAddressMode.Repeat or WGPUAddressMode.MirrorRepeat or WGPUAddressMode.ClampToEdge)
            || sampling.AddressV is not (WGPUAddressMode.Repeat or WGPUAddressMode.MirrorRepeat or WGPUAddressMode.ClampToEdge)
            || sampling.MinFilter is not (WGPUFilterMode.Nearest or WGPUFilterMode.Linear)
            || sampling.MagFilter is not (WGPUFilterMode.Nearest or WGPUFilterMode.Linear)
            || sampling.MipFilter is not (WGPUMipmapFilterMode.Nearest or WGPUMipmapFilterMode.Linear)) {
            throw new ArgumentException("Invalid PBR texture dimensions, mip levels or sampler.");
        }
        var w = width; var h = height;
        for (var i = 0; i < levels.Length; i++) {
            if (levels[i].Length != (long)w * h * 4 || (i > 0 && (width >> (i - 1)) <= 1 && (height >> (i - 1)) <= 1)) {
                throw new ArgumentException("PBR textures require contiguous RGBA8 mip levels.", nameof(levels));
            }
            w = System.Math.Max(1u, w / 2); h = System.Math.Max(1u, h / 2);
        }
        var copied = new ReadOnlyMemory<byte>[levels.Length];
        for (var i = 0; i < copied.Length; i++) { copied[i] = levels[i].ToArray(); }
        return new(width, height, srgb, sampling, copied);
    }
}
