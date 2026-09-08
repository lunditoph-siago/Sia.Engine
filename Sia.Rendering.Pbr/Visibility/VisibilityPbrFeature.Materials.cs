using System.Runtime.InteropServices;
using Sia;
using Sia.Graphics.Reactive;
using Sia.Math;
using Sia.RenderGraph;
using Sia.WebGPU;

namespace Sia.Engine.Rendering.Pbr;

public sealed partial class VisibilityPbrFeature
{
    public const int MaximumMaterialCount = 128;
    public const ulong MaximumMaterialTextureBytes = 128 * 1024 * 1024;
    public int MaterialCount => _materials.Length;

    private static readonly PbrTextureData s_WhiteColor = PbrTextureData.Create(1, 1, true, new ReadOnlyMemory<byte>[] { new byte[] { 255, 255, 255, 255 } });
    private static readonly PbrTextureData s_WhiteData = PbrTextureData.Create(1, 1, false, new ReadOnlyMemory<byte>[] { new byte[] { 255, 255, 255, 255 } });
    private static readonly PbrTextureData s_FlatNormal = PbrTextureData.Create(1, 1, false, new ReadOnlyMemory<byte>[] { new byte[] { 128, 128, 255, 255 } });

    private static PbrMaterialAsset[] ValidateMaterials(VisibilityAlbedo? albedo, ReadOnlySpan<PbrMaterialAsset> materials)
    {
        if (materials.Length > MaximumMaterialCount) {
            throw new ArgumentOutOfRangeException(nameof(materials), $"Visibility supports at most {MaximumMaterialCount} resident material batches.");
        }
        if (!materials.IsEmpty) { return PbrSceneAsset.Create([], materials, []).Materials.ToArray(); }
        ArgumentNullException.ThrowIfNull(albedo);
        ArgumentNullException.ThrowIfNull(albedo.MipLevels);
        return [new(new(float3.one, 1, 1, float3.one, 1), PbrTextureData.Create(albedo.Width, albedo.Height, false, albedo.MipLevels))];
    }

    private static PbrTextureData[] MaterialMaps(PbrMaterialAsset material) => [
        material.BaseColor ?? s_WhiteColor, material.Normal ?? s_FlatNormal,
        material.MetallicRoughness ?? s_WhiteData, material.Occlusion ?? s_WhiteData, material.Emissive ?? s_WhiteColor
    ];

    private static void ValidateTextureCapacity(PbrMaterialAsset[] materials, WGPULimits limits)
    {
        var textures = new HashSet<PbrTextureData>(ReferenceEqualityComparer.Instance);
        ulong bytes = 0;
        foreach (var material in materials) {
            if (!Finite(material.Parameters.EmissiveColor * material.Parameters.EmissiveStrength)) {
                throw new ArgumentException("Material emission exceeds its GPU representation.", nameof(materials));
            }
            foreach (var texture in MaterialMaps(material)) {
                if (!textures.Add(texture)) { continue; }
                if (texture.Width > limits.MaxTextureDimension2D || texture.Height > limits.MaxTextureDimension2D) {
                    throw new ArgumentException("Material texture dimensions exceed the device limits.", nameof(materials));
                }
                foreach (var level in texture.MipLevels.Span) { bytes = checked(bytes + (uint)level.Length); }
            }
        }
        if (bytes > MaximumMaterialTextureBytes) { throw new ArgumentException("Resident material textures exceed the 128 MiB budget.", nameof(materials)); }
    }

    private static (MaterialGpu[] Materials, MaterialTextureGpu[] Textures) CreateMaterials(World world,
        WgpuHandle<WGPUDevice> device, WgpuHandle<WGPUQueue> queue, PbrMaterialAsset[] materials, WGPULimits limits, List<Entity> acquired)
    {
        var textures = new Dictionary<PbrTextureData, MaterialTextureGpu>(ReferenceEqualityComparer.Instance);
        var result = new MaterialGpu[materials.Length];
        for (var i = 0; i < materials.Length; i++) {
            var source = materials[i];
            var maps = MaterialMaps(source).Select(texture => {
                if (!textures.TryGetValue(texture, out var gpu)) {
                    gpu = CreateMaterialTexture(world, device, queue, texture, textures.Count, acquired);
                    textures.Add(texture, gpu);
                }
                return gpu;
            }).ToArray();
            var p = source.Parameters;
            var uniform = Upload<MaterialParametersGpu>(world, device, queue, [new(new(p.BaseColor, p.Metallic),
                new(p.EmissiveColor * p.EmissiveStrength, p.Roughness), new(source.NormalScale, source.OcclusionStrength, source.Normal is null ? 0 : 1, i))],
                WGPUBufferUsage.Uniform, limits, acquired);
            result[i] = new(uniform, new($"visibility-material-{i}"), maps);
        }
        return (result, textures.Values.ToArray());
    }

    private static unsafe MaterialTextureGpu CreateMaterialTexture(World world, WgpuHandle<WGPUDevice> device,
        WgpuHandle<WGPUQueue> queue, PbrTextureData source, int index, List<Entity> acquired)
    {
        var descriptor = WGPUTextureDescriptor.Default;
        descriptor.Dimension = WGPUTextureDimension._2D;
        descriptor.Size = new() { Width = source.Width, Height = source.Height, DepthOrArrayLayers = 1 };
        descriptor.Format = source.Srgb ? WGPUTextureFormat.RGBA8UnormSrgb : WGPUTextureFormat.RGBA8Unorm;
        descriptor.Usage = WGPUTextureUsage.TextureBinding | WGPUTextureUsage.CopyDst;
        descriptor.MipLevelCount = (uint)source.MipLevels.Length;
        var texture = Own(world, Wgpu.CreateTexture(device, descriptor), acquired);
        var width = source.Width; var height = source.Height;
        for (var level = 0; level < source.MipLevels.Length; level++) {
            var destination = new WGPUTexelCopyTextureInfo { Texture = (WGPUTexture*)texture.GetWgpu<WGPUTexture>().DangerousGetHandle(),
                MipLevel = (uint)level, Aspect = WGPUTextureAspect.All };
            var layout = new WGPUTexelCopyBufferLayout { BytesPerRow = width * 4, RowsPerImage = height };
            var extent = new WGPUExtent3D { Width = width, Height = height, DepthOrArrayLayers = 1 };
            fixed (byte* pixels = source.MipLevels.Span[level].Span) {
                WgpuUnsafe.wgpuQueueWriteTexture((WGPUQueue*)queue.DangerousGetHandle(), &destination,
                    pixels, (nuint)source.MipLevels.Span[level].Length, &layout, &extent);
            }
            width = System.Math.Max(1, width / 2); height = System.Math.Max(1, height / 2);
        }
        var view = Own(world, Wgpu.CreateTextureView(texture.GetWgpu<WGPUTexture>(), WGPUTextureViewDescriptor.Default), acquired);
        var sampling = source.Sampler;
        var samplerDescriptor = WGPUSamplerDescriptor.Default;
        samplerDescriptor.AddressModeU = sampling.AddressU; samplerDescriptor.AddressModeV = sampling.AddressV;
        samplerDescriptor.MagFilter = sampling.MagFilter; samplerDescriptor.MinFilter = sampling.MinFilter;
        samplerDescriptor.MipmapFilter = sampling.MipFilter;
        samplerDescriptor.LodMaxClamp = sampling.UseMipmaps ? source.MipLevels.Length - 1 : 0;
        var sampler = Own(world, Wgpu.CreateSampler(device, samplerDescriptor), acquired);
        return new(texture, view, sampler, new($"visibility-material-texture-{index}"), source.Srgb);
    }

    private sealed record MaterialGpu(Entity Uniform, RenderGraphBufferKey Key, MaterialTextureGpu[] Maps);
    private sealed record MaterialTextureGpu(Entity Texture, Entity View, Entity Sampler, RenderGraphTextureKey Key, bool Srgb);
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct MaterialParametersGpu(float4 ColorMetallic, float4 EmissiveRoughness, float4 TextureFactors);
}
