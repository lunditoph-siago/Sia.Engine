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
    public int MaterialCount { get; }
    private readonly Entity _materialParameters;
    private static readonly RenderGraphBufferKey s_MaterialParametersKey = new("visibility-material-parameters");

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

    private static (MaterialBatchGpu[] Materials, MaterialTextureGpu[] Textures, Entity Parameters) CreateMaterials(World world,
        WgpuHandle<WGPUDevice> device, WgpuHandle<WGPUQueue> queue, PbrMaterialAsset[] materials, WGPULimits limits, List<Entity> acquired)
    {
        var references = new Dictionary<PbrTextureData, (MaterialTextureGpu Texture, uint Layer)>(ReferenceEqualityComparer.Instance);
        var textures = new List<MaterialTextureGpu>();
        foreach (var shape in materials.SelectMany(MaterialMaps).Distinct<PbrTextureData>(ReferenceEqualityComparer.Instance)
            .GroupBy(texture => (texture.Width, texture.Height, texture.MipLevels.Length, texture.Srgb, texture.Sampler))) {
            foreach (var page in shape.Chunk(checked((int)limits.MaxTextureArrayLayers))) {
                var texture = CreateMaterialTexture(world, device, queue, page, textures.Count, acquired);
                textures.Add(texture);
                for (var layer = 0; layer < page.Length; layer++) { references.Add(page[layer], (texture, (uint)layer)); }
            }
        }
        var batches = new Dictionary<(MaterialTextureGpu, MaterialTextureGpu, MaterialTextureGpu, MaterialTextureGpu, MaterialTextureGpu), int>();
        var result = new List<MaterialBatchGpu>();
        var parameters = new MaterialParametersGpu[materials.Length];
        for (var i = 0; i < materials.Length; i++) {
            var source = materials[i];
            var maps = MaterialMaps(source).Select(texture => references[texture]).ToArray();
            var key = (maps[0].Texture, maps[1].Texture, maps[2].Texture, maps[3].Texture, maps[4].Texture);
            if (!batches.TryGetValue(key, out var batch)) {
                batch = batches.Count;
                batches.Add(key, batch);
                var uniform = Upload<uint4>(world, device, queue, [new((uint)batch, 0, 0, 0)], WGPUBufferUsage.Uniform, limits, acquired);
                result.Add(new(uniform, new($"visibility-material-batch-{batch}"), maps.Select(map => map.Texture).ToArray()));
            }
            var p = source.Parameters;
            parameters[i] = new(new(p.BaseColor, p.Metallic), new(p.EmissiveColor * p.EmissiveStrength, p.Roughness),
                new(source.NormalScale, source.OcclusionStrength, source.Normal is null ? 0 : 1, 0),
                new(maps[0].Layer, maps[1].Layer, maps[2].Layer, maps[3].Layer), new(maps[4].Layer, (uint)batch, 0, 0));
        }
        return (result.ToArray(), textures.ToArray(), Upload<MaterialParametersGpu>(world, device, queue, parameters,
            WGPUBufferUsage.Storage, limits, acquired));
    }

    private static unsafe MaterialTextureGpu CreateMaterialTexture(World world, WgpuHandle<WGPUDevice> device,
        WgpuHandle<WGPUQueue> queue, ReadOnlySpan<PbrTextureData> sources, int index, List<Entity> acquired)
    {
        var source = sources[0];
        var bindingDimension = WGPUTextureBindingViewDimension.Default;
        bindingDimension.TextureBindingViewDimension = WGPUTextureViewDimension._2DArray;
        var descriptor = WGPUTextureDescriptor.Default;
        descriptor.NextInChain = &bindingDimension.Chain;
        descriptor.Dimension = WGPUTextureDimension._2D;
        descriptor.Size = new() { Width = source.Width, Height = source.Height, DepthOrArrayLayers = (uint)sources.Length };
        descriptor.Format = source.Srgb ? WGPUTextureFormat.RGBA8UnormSrgb : WGPUTextureFormat.RGBA8Unorm;
        descriptor.Usage = WGPUTextureUsage.TextureBinding | WGPUTextureUsage.CopyDst;
        descriptor.MipLevelCount = (uint)source.MipLevels.Length;
        var texture = Own(world, Wgpu.CreateTexture(device, descriptor), acquired);
        for (var layer = 0; layer < sources.Length; layer++) {
            var width = source.Width; var height = source.Height;
            for (var level = 0; level < source.MipLevels.Length; level++) {
                var destination = new WGPUTexelCopyTextureInfo { Texture = (WGPUTexture*)texture.GetWgpu<WGPUTexture>().DangerousGetHandle(),
                    MipLevel = (uint)level, Origin = new() { Z = (uint)layer }, Aspect = WGPUTextureAspect.All };
                var layout = new WGPUTexelCopyBufferLayout { BytesPerRow = width * 4, RowsPerImage = height };
                var extent = new WGPUExtent3D { Width = width, Height = height, DepthOrArrayLayers = 1 };
                fixed (byte* pixels = sources[layer].MipLevels.Span[level].Span) {
                    WgpuUnsafe.wgpuQueueWriteTexture((WGPUQueue*)queue.DangerousGetHandle(), &destination,
                        pixels, (nuint)sources[layer].MipLevels.Span[level].Length, &layout, &extent);
                }
                width = System.Math.Max(1, width / 2); height = System.Math.Max(1, height / 2);
            }
        }
        var viewDescriptor = WGPUTextureViewDescriptor.Default;
        viewDescriptor.Dimension = WGPUTextureViewDimension._2DArray;
        viewDescriptor.ArrayLayerCount = (uint)sources.Length;
        var view = Own(world, Wgpu.CreateTextureView(texture.GetWgpu<WGPUTexture>(), viewDescriptor), acquired);
        var sampling = source.Sampler;
        var samplerDescriptor = WGPUSamplerDescriptor.Default;
        samplerDescriptor.AddressModeU = sampling.AddressU; samplerDescriptor.AddressModeV = sampling.AddressV;
        samplerDescriptor.MagFilter = sampling.MagFilter; samplerDescriptor.MinFilter = sampling.MinFilter;
        samplerDescriptor.MipmapFilter = sampling.MipFilter;
        samplerDescriptor.LodMaxClamp = sampling.UseMipmaps ? source.MipLevels.Length - 1 : 0;
        var sampler = Own(world, Wgpu.CreateSampler(device, samplerDescriptor), acquired);
        return new(texture, view, sampler, new($"visibility-material-texture-{index}"), source.Srgb);
    }

    private sealed record MaterialBatchGpu(Entity Uniform, RenderGraphBufferKey Key, MaterialTextureGpu[] Maps);
    private sealed record MaterialTextureGpu(Entity Texture, Entity View, Entity Sampler, RenderGraphTextureKey Key, bool Srgb);
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct MaterialParametersGpu(float4 ColorMetallic, float4 EmissiveRoughness, float4 TextureFactors,
        uint4 Layers, uint4 Indices);
}
