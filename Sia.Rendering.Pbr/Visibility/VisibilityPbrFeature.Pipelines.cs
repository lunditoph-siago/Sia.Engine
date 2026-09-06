using Sia;
using Sia.WebGPU;

namespace Sia.Engine.Rendering.Pbr;

public sealed partial class VisibilityPbrFeature
{
    private static WGPUBindGroupLayoutEntry BufferLayout(uint binding, WGPUBufferBindingType type, ulong size,
        WGPUShaderStage stages = WGPUShaderStage.Vertex | WGPUShaderStage.Compute)
    {
        var entry = WGPUBindGroupLayoutEntry.Default;
        entry.Binding = binding;
        entry.Visibility = stages;
        entry.Buffer.Type = type;
        entry.Buffer.MinBindingSize = size;
        return entry;
    }

    private static WGPUBindGroupLayoutEntry TextureLayout(uint binding, WGPUTextureSampleType type, WGPUShaderStage stages)
    {
        var entry = WGPUBindGroupLayoutEntry.Default;
        entry.Binding = binding;
        entry.Visibility = stages;
        entry.Texture.SampleType = type;
        entry.Texture.ViewDimension = WGPUTextureViewDimension._2D;
        return entry;
    }

    private static unsafe Entity Layout(World world, WgpuHandle<WGPUDevice> device,
        ReadOnlySpan<WGPUBindGroupLayoutEntry> entries, List<Entity> acquired)
    {
        fixed (WGPUBindGroupLayoutEntry* pointer = entries) {
            var descriptor = WGPUBindGroupLayoutDescriptor.Default;
            descriptor.EntryCount = (nuint)entries.Length;
            descriptor.Entries = pointer;
            return Own(world, Wgpu.CreateBindGroupLayout(device, descriptor), acquired);
        }
    }

    private static Entity CreateGeometryLayout(World world, WgpuHandle<WGPUDevice> device, List<Entity> acquired) =>
        Layout(world, device, [
            BufferLayout(0, WGPUBufferBindingType.Uniform, 128),
            BufferLayout(1, WGPUBufferBindingType.ReadOnlyStorage, 48),
            BufferLayout(2, WGPUBufferBindingType.ReadOnlyStorage, 16),
            BufferLayout(3, WGPUBufferBindingType.ReadOnlyStorage, 4),
            BufferLayout(4, WGPUBufferBindingType.ReadOnlyStorage, 16),
            BufferLayout(5, WGPUBufferBindingType.ReadOnlyStorage, 176),
            BufferLayout(6, WGPUBufferBindingType.ReadOnlyStorage, 16)
        ], acquired);

    private static Entity CreateResolveLayout(World world, WgpuHandle<WGPUDevice> device, List<Entity> acquired)
    {
        var hdr = WGPUBindGroupLayoutEntry.Default;
        hdr.Binding = 1;
        hdr.Visibility = WGPUShaderStage.Compute;
        hdr.StorageTexture.Access = WGPUStorageTextureAccess.WriteOnly;
        hdr.StorageTexture.Format = WGPUTextureFormat.RGBA16Float;
        hdr.StorageTexture.ViewDimension = WGPUTextureViewDimension._2D;
        var sampler = WGPUBindGroupLayoutEntry.Default;
        sampler.Binding = 3;
        sampler.Visibility = WGPUShaderStage.Compute;
        sampler.Sampler.Type = WGPUSamplerBindingType.Filtering;
        return Layout(world, device, [
            TextureLayout(0, WGPUTextureSampleType.Uint, WGPUShaderStage.Compute), hdr,
            TextureLayout(2, WGPUTextureSampleType.Float, WGPUShaderStage.Compute), sampler
        ], acquired);
    }

    private static unsafe Entity PipelineLayout(World world, WgpuHandle<WGPUDevice> device,
        ReadOnlySpan<Entity> layouts, List<Entity> acquired)
    {
        var pointers = stackalloc WGPUBindGroupLayout*[layouts.Length];
        for (var i = 0; i < layouts.Length; i++) {
            pointers[i] = (WGPUBindGroupLayout*)layouts[i].GetWgpu<WGPUBindGroupLayout>().DangerousGetHandle();
        }
        var descriptor = WGPUPipelineLayoutDescriptor.Default;
        descriptor.BindGroupLayoutCount = (nuint)layouts.Length;
        descriptor.BindGroupLayouts = pointers;
        return Own(world, Wgpu.CreatePipelineLayout(device, descriptor), acquired);
    }

    private static unsafe Entity CreateRaster(World world, WgpuHandle<WGPUDevice> device,
        Entity layout, List<Entity> acquired)
    {
        var shader = Own(world, Wgpu.CreateWgslShaderModule(device, PbrShaderSource.LoadVisibilityRaster(), "visibility-raster"), acquired);
        var pipelineLayout = PipelineLayout(world, device, [layout], acquired);
        fixed (byte* vertexName = "vertex"u8)
        fixed (byte* fragmentName = "fragment"u8) {
            var target = WGPUColorTargetState.Default;
            target.Format = WGPUTextureFormat.R32Uint;
            target.WriteMask = WGPUColorWriteMask.All;
            var fragment = WGPUFragmentState.Default;
            fragment.Module = (WGPUShaderModule*)shader.GetWgpu<WGPUShaderModule>().DangerousGetHandle();
            fragment.EntryPoint = new WGPUStringView { Data = fragmentName, Length = 8 };
            fragment.TargetCount = 1;
            fragment.Targets = &target;
            var depth = WGPUDepthStencilState.Default;
            depth.Format = WGPUTextureFormat.Depth32Float;
            depth.DepthWriteEnabled = WGPUOptionalBool.True;
            depth.DepthCompare = WGPUCompareFunction.Less;
            var descriptor = WGPURenderPipelineDescriptor.Default;
            descriptor.Layout = (WGPUPipelineLayout*)pipelineLayout.GetWgpu<WGPUPipelineLayout>().DangerousGetHandle();
            descriptor.Vertex = WGPUVertexState.Default;
            descriptor.Vertex.Module = fragment.Module;
            descriptor.Vertex.EntryPoint = new WGPUStringView { Data = vertexName, Length = 6 };
            descriptor.Fragment = &fragment;
            descriptor.DepthStencil = &depth;
            descriptor.Primitive = WGPUPrimitiveState.Default;
            descriptor.Primitive.Topology = WGPUPrimitiveTopology.TriangleList;
            descriptor.Primitive.FrontFace = WGPUFrontFace.CCW;
            descriptor.Primitive.CullMode = WGPUCullMode.Back;
            descriptor.Multisample = WGPUMultisampleState.Default;
            return Own(world, Wgpu.CreateRenderPipeline(device, descriptor), acquired);
        }
    }

    private static unsafe Entity CreateResolve(World world, WgpuHandle<WGPUDevice> device,
        Entity geometry, Entity textures, List<Entity> acquired)
    {
        var shader = Own(world, Wgpu.CreateWgslShaderModule(device, PbrShaderSource.LoadVisibilityResolve(), "visibility-resolve"), acquired);
        var layout = PipelineLayout(world, device, [geometry, textures], acquired);
        fixed (byte* entry = "resolve"u8) {
            var descriptor = WGPUComputePipelineDescriptor.Default;
            descriptor.Layout = (WGPUPipelineLayout*)layout.GetWgpu<WGPUPipelineLayout>().DangerousGetHandle();
            descriptor.Compute = WGPUComputeState.Default;
            descriptor.Compute.Module = (WGPUShaderModule*)shader.GetWgpu<WGPUShaderModule>().DangerousGetHandle();
            descriptor.Compute.EntryPoint = new WGPUStringView { Data = entry, Length = 7 };
            return Own(world, Wgpu.CreateComputePipeline(device, descriptor), acquired);
        }
    }

    private static OutputGpu CreateOutput(World world, WgpuHandle<WGPUDevice> device,
        WGPUTextureFormat format, List<Entity> acquired)
    {
        var layout = Layout(world, device, [
            BufferLayout(0, WGPUBufferBindingType.Uniform, 16, WGPUShaderStage.Fragment),
            TextureLayout(1, WGPUTextureSampleType.Float, WGPUShaderStage.Fragment)
        ], acquired);
        var pipelineLayout = PipelineLayout(world, device, [layout], acquired);
        var shader = Own(world, Wgpu.CreateWgslShaderModule(device, PbrShaderSource.LoadToneMapping(), "visibility-output"), acquired);
        var pipeline = Own(world, PbrIblPrecomputePipelines.CreateFullscreenPipeline(device,
            shader.GetWgpu<WGPUShaderModule>(), pipelineLayout.GetWgpu<WGPUPipelineLayout>(), format), acquired);
        return new(pipeline, layout, format is WGPUTextureFormat.RGBA8Unorm or WGPUTextureFormat.BGRA8Unorm);
    }

    private readonly record struct OutputGpu(Entity Pipeline, Entity Layout, bool EncodeSrgb);
}
