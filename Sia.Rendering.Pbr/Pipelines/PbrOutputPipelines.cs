using Sia;
using Sia.WebGPU;

namespace Sia.Engine.Rendering.Pbr;

public sealed unsafe class PbrOutputPipelines
{
    public const WGPUTextureFormat HdrFormat = WGPUTextureFormat.RGBA16Float;

    internal Entity SkyboxPipeline { get; }
    internal Entity SkyboxLayout { get; }
    internal Entity ToneMappingPipeline { get; }
    internal Entity ToneMappingLayout { get; }
    internal bool EncodeSrgb { get; }

    private PbrOutputPipelines(Entity skybox, Entity skyboxLayout, Entity toneMapping,
        Entity toneMappingLayout, bool encodeSrgb)
    {
        SkyboxPipeline = skybox;
        SkyboxLayout = skyboxLayout;
        ToneMappingPipeline = toneMapping;
        ToneMappingLayout = toneMappingLayout;
        EncodeSrgb = encodeSrgb;
    }

    public static PbrOutputPipelines Create(World world, Entity device, WGPUTextureFormat outputFormat)
    {
        ArgumentNullException.ThrowIfNull(world);
        var encodeSrgb = outputFormat switch {
            WGPUTextureFormat.RGBA8Unorm or WGPUTextureFormat.BGRA8Unorm => true,
            WGPUTextureFormat.RGBA8UnormSrgb or WGPUTextureFormat.BGRA8UnormSrgb => false,
            _ => throw new ArgumentOutOfRangeException(nameof(outputFormat), "PBR output requires an SDR RGBA8 or BGRA8 target.")
        };
        var handle = device.GetWgpu<WGPUDevice>();
        var skyEntries = stackalloc WGPUBindGroupLayoutEntry[3];
        skyEntries[0] = UniformEntry(64);
        skyEntries[1] = TextureEntry(WGPUTextureViewDimension.Cube);
        skyEntries[2] = WGPUBindGroupLayoutEntry.Default;
        skyEntries[2].Binding = 2;
        skyEntries[2].Visibility = WGPUShaderStage.Fragment;
        skyEntries[2].Sampler.Type = WGPUSamplerBindingType.Filtering;
        var layoutDescriptor = WGPUBindGroupLayoutDescriptor.Default;
        layoutDescriptor.EntryCount = 3;
        layoutDescriptor.Entries = skyEntries;
        var skyLayout = world.OwnWgpu(Wgpu.CreateBindGroupLayout(handle, layoutDescriptor));
        var toneEntries = stackalloc WGPUBindGroupLayoutEntry[2];
        toneEntries[0] = UniformEntry(16);
        toneEntries[1] = TextureEntry(WGPUTextureViewDimension._2D);
        layoutDescriptor.EntryCount = 2;
        layoutDescriptor.Entries = toneEntries;
        var toneLayout = world.OwnWgpu(Wgpu.CreateBindGroupLayout(handle, layoutDescriptor));
        return new PbrOutputPipelines(
            CreatePipeline(world, handle, skyLayout, PbrShaderSource.LoadSkybox(), HdrFormat), skyLayout,
            CreatePipeline(world, handle, toneLayout, PbrShaderSource.LoadToneMapping(), outputFormat), toneLayout,
            encodeSrgb);
    }

    private static Entity CreatePipeline(World world, WgpuHandle<WGPUDevice> device,
        Entity layout, string source, WGPUTextureFormat format)
    {
        var shader = world.OwnWgpu(Wgpu.CreateWgslShaderModule(device, source));
        var bindLayout = (WGPUBindGroupLayout*)layout.GetWgpu<WGPUBindGroupLayout>().DangerousGetHandle();
        var descriptor = WGPUPipelineLayoutDescriptor.Default;
        descriptor.BindGroupLayoutCount = 1;
        descriptor.BindGroupLayouts = &bindLayout;
        var pipelineLayout = world.OwnWgpu(Wgpu.CreatePipelineLayout(device, descriptor));
        return world.OwnWgpu(PbrIblPrecomputePipelines.CreateFullscreenPipeline(
            device, shader.GetWgpu<WGPUShaderModule>(), pipelineLayout.GetWgpu<WGPUPipelineLayout>(), format));
    }

    private static WGPUBindGroupLayoutEntry UniformEntry(ulong size)
    {
        var entry = WGPUBindGroupLayoutEntry.Default;
        entry.Binding = 0;
        entry.Visibility = WGPUShaderStage.Fragment;
        entry.Buffer.Type = WGPUBufferBindingType.Uniform;
        entry.Buffer.MinBindingSize = size;
        return entry;
    }

    private static WGPUBindGroupLayoutEntry TextureEntry(WGPUTextureViewDimension dimension)
    {
        var entry = WGPUBindGroupLayoutEntry.Default;
        entry.Binding = 1;
        entry.Visibility = WGPUShaderStage.Fragment;
        entry.Texture.SampleType = WGPUTextureSampleType.Float;
        entry.Texture.ViewDimension = dimension;
        return entry;
    }
}
