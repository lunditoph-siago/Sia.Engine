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
            BufferLayout(0, WGPUBufferBindingType.Uniform, 160),
            BufferLayout(1, WGPUBufferBindingType.ReadOnlyStorage, 48),
            BufferLayout(3, WGPUBufferBindingType.ReadOnlyStorage, 4),
            BufferLayout(4, WGPUBufferBindingType.ReadOnlyStorage, 8),
            BufferLayout(5, WGPUBufferBindingType.ReadOnlyStorage, 192),
            BufferLayout(6, WGPUBufferBindingType.ReadOnlyStorage, 8)
        ], acquired);

    private static Entity CreateResolveLayout(World world, WgpuHandle<WGPUDevice> device, List<Entity> acquired)
    {
        var hdr = WGPUBindGroupLayoutEntry.Default;
        hdr.Binding = 1;
        hdr.Visibility = WGPUShaderStage.Compute;
        hdr.StorageTexture.Access = WGPUStorageTextureAccess.WriteOnly;
        hdr.StorageTexture.Format = WGPUTextureFormat.RGBA16Float;
        hdr.StorageTexture.ViewDimension = WGPUTextureViewDimension._2D;
        var entries = new WGPUBindGroupLayoutEntry[18];
        entries[0] = TextureLayout(0, WGPUTextureSampleType.Uint, WGPUShaderStage.Compute);
        entries[1] = hdr;
        for (uint map = 0; map < 5; map++) {
            entries[2 + map * 2] = TextureLayout(2 + map * 2, WGPUTextureSampleType.Float, WGPUShaderStage.Compute);
            entries[2 + map * 2].Texture.ViewDimension = WGPUTextureViewDimension._2DArray;
            var sampler = WGPUBindGroupLayoutEntry.Default;
            sampler.Binding = 3 + map * 2;
            sampler.Visibility = WGPUShaderStage.Compute;
            sampler.Sampler.Type = WGPUSamplerBindingType.Filtering;
            entries[3 + map * 2] = sampler;
        }
        entries[12] = BufferLayout(12, WGPUBufferBindingType.Uniform, 16, WGPUShaderStage.Compute);
        for (uint i = 13; i < 16; i++) { entries[i] = hdr; entries[i].Binding = i; }
        entries[16] = BufferLayout(16, WGPUBufferBindingType.ReadOnlyStorage, 4, WGPUShaderStage.Compute);
        entries[17] = BufferLayout(17, WGPUBufferBindingType.ReadOnlyStorage, 80, WGPUShaderStage.Compute);
        return Layout(world, device, entries, acquired);
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
        Entity layout, List<Entity> acquired, bool shadow = false, bool indexed = false)
    {
        var first = indexed && !shadow && WgpuUnsafe.wgpuDeviceHasFeature((WGPUDevice*)device.DangerousGetHandle(), WGPUFeatureName.CoreFeaturesAndLimits) != 0;
        var shader = Own(world, Wgpu.CreateWgslShaderModule(device, PbrShaderSource.LoadVisibilityRaster(), "visibility-raster"), acquired);
        var pipelineLayout = PipelineLayout(world, device, [layout], acquired);
        var entry = first ? "indexed_first"u8 : indexed ? (shadow ? "indexed_shadow"u8 : "indexed"u8) : "vertex"u8;
        var fragmentEntry = first ? "fragment_first"u8 : "fragment"u8;
        fixed (byte* vertexName = entry)
        fixed (byte* fragmentName = fragmentEntry) {
            var target = WGPUColorTargetState.Default;
            target.Format = WGPUTextureFormat.R32Uint;
            target.WriteMask = WGPUColorWriteMask.All;
            var fragment = WGPUFragmentState.Default;
            fragment.Module = (WGPUShaderModule*)shader.GetWgpu<WGPUShaderModule>().DangerousGetHandle();
            fragment.EntryPoint = new WGPUStringView { Data = fragmentName, Length = (nuint)fragmentEntry.Length };
            fragment.TargetCount = 1;
            fragment.Targets = &target;
            var depth = WGPUDepthStencilState.Default;
            depth.Format = WGPUTextureFormat.Depth32Float;
            depth.DepthWriteEnabled = WGPUOptionalBool.True;
            depth.DepthCompare = WGPUCompareFunction.Less;
            depth.DepthBias = shadow ? 2 : 0;
            depth.DepthBiasSlopeScale = shadow ? 2 : 0;
            var descriptor = WGPURenderPipelineDescriptor.Default;
            descriptor.Layout = (WGPUPipelineLayout*)pipelineLayout.GetWgpu<WGPUPipelineLayout>().DangerousGetHandle();
            descriptor.Vertex = WGPUVertexState.Default;
            descriptor.Vertex.Module = fragment.Module;
            descriptor.Vertex.EntryPoint = new WGPUStringView { Data = vertexName, Length = (nuint)entry.Length };
            descriptor.Fragment = shadow ? null : &fragment;
            descriptor.DepthStencil = &depth;
            descriptor.Primitive = WGPUPrimitiveState.Default;
            descriptor.Primitive.Topology = WGPUPrimitiveTopology.TriangleList;
            descriptor.Primitive.FrontFace = WGPUFrontFace.CCW;
            descriptor.Primitive.CullMode = WGPUCullMode.Back;
            descriptor.Multisample = WGPUMultisampleState.Default;
            return Own(world, Wgpu.CreateRenderPipeline(device, descriptor), acquired);
        }
    }

    private readonly record struct ResolveGpu(Entity Debug, Entity Surface);

    private static ResolveGpu CreateResolve(World world, WgpuHandle<WGPUDevice> device,
        Entity geometry, Entity textures, List<Entity> acquired)
    {
        var shader = Own(world, Wgpu.CreateWgslShaderModule(device, PbrShaderSource.LoadVisibilityResolve(), "visibility-resolve"), acquired);
        var layout = PipelineLayout(world, device, [geometry, textures], acquired);
        return new(ComputePipeline(world, device, shader, layout, "resolve", acquired),
            ComputePipeline(world, device, shader, layout, "resolve_surface", acquired));
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
