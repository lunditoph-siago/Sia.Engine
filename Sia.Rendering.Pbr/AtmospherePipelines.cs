using Sia;
using Sia.WebGPU;

namespace Sia.Engine.Rendering.Pbr;

internal sealed unsafe class AtmospherePipelines
{
    public Entity CommonLayout { get; }
    public Entity[] Compute { get; }
    public Entity[] ComputeLayouts { get; }
    public Entity Skybox { get; }
    public Entity Prefilter { get; }
    public Entity PrefilterLayout { get; }
    public Entity Composite { get; }
    public Entity CompositeLayout { get; }

    public AtmospherePipelines(in GpuFrame frame)
    {
        CommonLayout = Layout(frame, [Uniform(0, AtmosphereUniformData.Stride), Texture(1), Texture(2), Texture(3), Sampler(4)]);
        ComputeLayouts = [
            Layout(frame, [StorageTexture(0, WGPUTextureViewDimension._2D)]),
            Layout(frame, [StorageTexture(0, WGPUTextureViewDimension._2D)]),
            Layout(frame, [StorageTexture(0, WGPUTextureViewDimension._2D)]),
            Layout(frame, [StorageTexture(0, WGPUTextureViewDimension._3D), StorageTexture(1, WGPUTextureViewDimension._3D)]),
            Layout(frame, [StorageBuffer(0)])
        ];
        string[] names = ["transmittance", "multiscatter", "sky_view", "aerial", "irradiance"];
        Compute = new Entity[names.Length];
        for (var i = 0; i < names.Length; i++) {
            Compute[i] = CreateCompute(frame, names[i], ComputeLayouts[i]);
        }
        Skybox = CreateRender(frame, "skybox", default);
        PrefilterLayout = Layout(frame, [Uniform(0, IblPrefilterParamsGpu.Stride)]);
        Prefilter = CreateRender(frame, "prefilter", PrefilterLayout);
        CompositeLayout = Layout(frame, [Texture(0), Texture(1, WGPUTextureViewDimension._2D, WGPUTextureSampleType.Depth),
            Texture(2, WGPUTextureViewDimension._3D), Texture(3, WGPUTextureViewDimension._3D)]);
        Composite = CreateRender(frame, "composite", CompositeLayout);
    }

    private Entity PipelineLayout(in GpuFrame frame, Entity extra)
    {
        var layouts = stackalloc WGPUBindGroupLayout*[2];
        layouts[0] = (WGPUBindGroupLayout*)CommonLayout.GetWgpu<WGPUBindGroupLayout>().DangerousGetHandle();
        layouts[1] = extra.IsValid ? (WGPUBindGroupLayout*)extra.GetWgpu<WGPUBindGroupLayout>().DangerousGetHandle() : null;
        var descriptor = WGPUPipelineLayoutDescriptor.Default;
        descriptor.BindGroupLayoutCount = extra.IsValid ? 2u : 1u;
        descriptor.BindGroupLayouts = layouts;
        return frame.ResourceWorld.OwnWgpu(Wgpu.CreatePipelineLayout(frame.Device.GetWgpu<WGPUDevice>(), descriptor));
    }

    private Entity CreateCompute(in GpuFrame frame, string name, Entity extra)
    {
        var shader = frame.ResourceWorld.OwnWgpu(Wgpu.CreateWgslShaderModule(frame.Device.GetWgpu<WGPUDevice>(), PbrShaderSource.LoadAtmosphere(name), "atmosphere-" + name));
        var layout = PipelineLayout(frame, extra);
        var entry = "compute"u8;
        fixed (byte* text = entry) {
            var descriptor = WGPUComputePipelineDescriptor.Default;
            descriptor.Layout = (WGPUPipelineLayout*)layout.GetWgpu<WGPUPipelineLayout>().DangerousGetHandle();
            descriptor.Compute = WGPUComputeState.Default;
            descriptor.Compute.Module = (WGPUShaderModule*)shader.GetWgpu<WGPUShaderModule>().DangerousGetHandle();
            descriptor.Compute.EntryPoint = new WGPUStringView { Data = text, Length = (nuint)entry.Length };
            return frame.ResourceWorld.OwnWgpu(Wgpu.CreateComputePipeline(frame.Device.GetWgpu<WGPUDevice>(), descriptor));
        }
    }

    private Entity CreateRender(in GpuFrame frame, string name, Entity extra)
    {
        var shader = frame.ResourceWorld.OwnWgpu(Wgpu.CreateWgslShaderModule(frame.Device.GetWgpu<WGPUDevice>(), PbrShaderSource.LoadAtmosphere(name), "atmosphere-" + name));
        var layout = PipelineLayout(frame, extra);
        return frame.ResourceWorld.OwnWgpu(PbrIblPrecomputePipelines.CreateFullscreenPipeline(frame.Device.GetWgpu<WGPUDevice>(),
            shader.GetWgpu<WGPUShaderModule>(), layout.GetWgpu<WGPUPipelineLayout>(), PbrOutputPipelines.HdrFormat));
    }

    private static Entity Layout(in GpuFrame frame, ReadOnlySpan<WGPUBindGroupLayoutEntry> entries)
    {
        fixed (WGPUBindGroupLayoutEntry* pointer = entries) {
            var descriptor = WGPUBindGroupLayoutDescriptor.Default;
            descriptor.EntryCount = (nuint)entries.Length;
            descriptor.Entries = pointer;
            return frame.ResourceWorld.OwnWgpu(Wgpu.CreateBindGroupLayout(frame.Device.GetWgpu<WGPUDevice>(), descriptor));
        }
    }

    private static WGPUBindGroupLayoutEntry Entry(uint binding) => WGPUBindGroupLayoutEntry.Default with {
        Binding = binding, Visibility = WGPUShaderStage.Compute | WGPUShaderStage.Fragment
    };

    private static WGPUBindGroupLayoutEntry Uniform(uint binding, ulong size)
    {
        var entry = Entry(binding);
        entry.Buffer.Type = WGPUBufferBindingType.Uniform;
        entry.Buffer.MinBindingSize = size;
        return entry;
    }

    private static WGPUBindGroupLayoutEntry Texture(uint binding, WGPUTextureViewDimension dimension = WGPUTextureViewDimension._2D,
        WGPUTextureSampleType type = WGPUTextureSampleType.Float)
    {
        var entry = Entry(binding);
        entry.Texture.ViewDimension = dimension;
        entry.Texture.SampleType = type;
        return entry;
    }

    private static WGPUBindGroupLayoutEntry Sampler(uint binding)
    {
        var entry = Entry(binding);
        entry.Sampler.Type = WGPUSamplerBindingType.Filtering;
        return entry;
    }

    private static WGPUBindGroupLayoutEntry StorageTexture(uint binding, WGPUTextureViewDimension dimension)
    {
        var entry = Entry(binding);
        entry.Visibility = WGPUShaderStage.Compute;
        entry.StorageTexture.Access = WGPUStorageTextureAccess.WriteOnly;
        entry.StorageTexture.Format = WGPUTextureFormat.RGBA16Float;
        entry.StorageTexture.ViewDimension = dimension;
        return entry;
    }

    private static WGPUBindGroupLayoutEntry StorageBuffer(uint binding)
    {
        var entry = Entry(binding);
        entry.Visibility = WGPUShaderStage.Compute;
        entry.Buffer.Type = WGPUBufferBindingType.Storage;
        entry.Buffer.MinBindingSize = IblShGpu.Stride;
        return entry;
    }
}
