using Sia;
using Sia.WebGPU;

namespace Sia.Engine.Rendering.Pbr;

internal sealed unsafe partial class AtmosphereGpuState
{
    private static readonly (uint Width, uint Height, uint Depth)[] _sizes = [(256, 64, 1), (32, 32, 1), (192, 108, 1), (32, 32, 32), (32, 32, 32)];

    public AtmosphereGpuState(in GpuFrame frame, PbrViewState state)
    {
        _pipelines = new AtmospherePipelines(frame);
        _uniform = frame.ResourceWorld.CreateWgpuBuffer(frame.Device, new WGPUBufferDescriptor {
            Size = AtmosphereUniformData.Stride, Usage = WGPUBufferUsage.Uniform | WGPUBufferUsage.CopyDst
        });
        _sampler = frame.ResourceWorld.CreateWgpuSampler(frame.Device, WGPUSamplerDescriptor.Default with {
            AddressModeU = WGPUAddressMode.ClampToEdge, AddressModeV = WGPUAddressMode.ClampToEdge, AddressModeW = WGPUAddressMode.ClampToEdge,
            MagFilter = WGPUFilterMode.Linear, MinFilter = WGPUFilterMode.Linear
        });
        _textures = new Entity[5];
        _views = new Entity[5];
        for (var i = 0; i < _sizes.Length; i++) {
            (_textures[i], _views[i]) = CreateTexture(frame, _sizes[i]);
        }
        var (_, dummy) = CreateTexture(frame, (1, 1, 1));
        _commonGroups = [CommonGroup(frame, dummy, dummy, dummy), CommonGroup(frame, _views[0], dummy, dummy),
            CommonGroup(frame, _views[0], _views[1], dummy), CommonGroup(frame, _views[0], _views[1], _views[2])];
        _outputGroups = new Entity[5];
        for (var i = 0; i < 3; i++) {
            _outputGroups[i] = BindGroup(frame, _pipelines.ComputeLayouts[i], [TextureEntry(0, _views[i].GetWgpu<WGPUTextureView>())]);
        }
        _outputGroups[3] = BindGroup(frame, _pipelines.ComputeLayouts[3], [TextureEntry(0, _views[3].GetWgpu<WGPUTextureView>()), TextureEntry(1, _views[4].GetWgpu<WGPUTextureView>())]);
        _outputGroups[4] = BindGroup(frame, _pipelines.ComputeLayouts[4], [BufferEntry(0, state.Ibl.ShBuffer, IblShGpu.Stride)]);
        _prefilterGroups = new Entity[state.IblPrefilterParamsBuffers.Length];
        for (var i = 0; i < _prefilterGroups.Length; i++) {
            _prefilterGroups[i] = BindGroup(frame, _pipelines.PrefilterLayout, [BufferEntry(0, state.IblPrefilterParamsBuffers[i], IblPrefilterParamsGpu.Stride)]);
        }
    }

    private static (Entity Texture, Entity View) CreateTexture(in GpuFrame frame, (uint Width, uint Height, uint Depth) size)
    {
        var texture = frame.ResourceWorld.CreateWgpuTexture(frame.Device, WGPUTextureDescriptor.Default with {
            Size = new WGPUExtent3D { Width = size.Width, Height = size.Height, DepthOrArrayLayers = size.Depth },
            Dimension = size.Depth > 1 ? WGPUTextureDimension._3D : WGPUTextureDimension._2D,
            Format = WGPUTextureFormat.RGBA16Float,
            Usage = WGPUTextureUsage.TextureBinding | WGPUTextureUsage.StorageBinding | WGPUTextureUsage.CopySrc
        });
        var view = frame.ResourceWorld.CreateWgpuTextureView(texture, WGPUTextureViewDescriptor.Default with {
            Format = WGPUTextureFormat.RGBA16Float, Dimension = size.Depth > 1 ? WGPUTextureViewDimension._3D : WGPUTextureViewDimension._2D
        });
        return (texture, view);
    }

    private Entity CommonGroup(in GpuFrame frame, Entity transmittance, Entity multiple, Entity sky)
    {
        var sampler = WGPUBindGroupEntry.Default;
        sampler.Binding = 4;
        sampler.Sampler = (WGPUSampler*)_sampler.GetWgpu<WGPUSampler>().DangerousGetHandle();
        return BindGroup(frame, _pipelines.CommonLayout, [BufferEntry(0, _uniform, AtmosphereUniformData.Stride),
            TextureEntry(1, transmittance.GetWgpu<WGPUTextureView>()), TextureEntry(2, multiple.GetWgpu<WGPUTextureView>()),
            TextureEntry(3, sky.GetWgpu<WGPUTextureView>()), sampler]);
    }

    private static WGPUBindGroupEntry BufferEntry(uint binding, Entity buffer, ulong size) => WGPUBindGroupEntry.Default with {
        Binding = binding, Buffer = (WGPUBuffer*)buffer.GetWgpu<WGPUBuffer>().DangerousGetHandle(), Size = size
    };

    private static WGPUBindGroupEntry TextureEntry(uint binding, WgpuHandle<WGPUTextureView> view) => WGPUBindGroupEntry.Default with {
        Binding = binding, TextureView = (WGPUTextureView*)view.DangerousGetHandle()
    };

    private static Entity BindGroup(in GpuFrame frame, Entity layout, ReadOnlySpan<WGPUBindGroupEntry> entries)
    {
        fixed (WGPUBindGroupEntry* pointer = entries) {
            var descriptor = WGPUBindGroupDescriptor.Default;
            descriptor.Layout = (WGPUBindGroupLayout*)layout.GetWgpu<WGPUBindGroupLayout>().DangerousGetHandle();
            descriptor.EntryCount = (nuint)entries.Length;
            descriptor.Entries = pointer;
            return frame.ResourceWorld.OwnWgpu(Wgpu.CreateBindGroup(frame.Device.GetWgpu<WGPUDevice>(), descriptor));
        }
    }
}
