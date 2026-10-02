using Sia;
using Sia.WebGPU;

namespace Sia.Engine.Rendering;

public static unsafe class GpuBinding
{
    public static WGPUBindGroupLayoutEntry Buffer(uint slot, WGPUBufferBindingType type,
        WGPUShaderStage stages, ulong minimum = 0) => new() {
            Binding = slot,
            Visibility = stages,
            Buffer = new() {
                Type = type,
                MinBindingSize = minimum
            }
        };

    public static WGPUBindGroupLayoutEntry Texture(uint slot, WGPUTextureSampleType type,
        WGPUShaderStage stages, WGPUTextureViewDimension dimension = WGPUTextureViewDimension._2D) => new() {
            Binding = slot,
            Visibility = stages,
            Texture = new() {
                SampleType = type,
                ViewDimension = dimension
            }
        };

    public static WGPUBindGroupLayoutEntry Sampler(uint slot, WGPUShaderStage stages) => new() {
        Binding = slot,
        Visibility = stages,
        Sampler = new() {
            Type = WGPUSamplerBindingType.Filtering
        }
    };

    public static WGPUBindGroupLayoutEntry StorageTexture(uint slot, WGPUTextureFormat format,
        WGPUTextureViewDimension dimension = WGPUTextureViewDimension._2D) => new() {
            Binding = slot,
            Visibility = WGPUShaderStage.Compute,
            StorageTexture = new() {
                Access = WGPUStorageTextureAccess.WriteOnly,
                Format = format,
                ViewDimension = dimension
            }
        };

    public static WGPUBindGroupEntry Buffer(uint slot, Entity buffer) => new() {
        Binding = slot,
        Buffer = (WGPUBuffer*)buffer.GetWgpu<WGPUBuffer>().DangerousGetHandle(),
        Size = buffer.Get<WgpuBufferInfo>().Size
    };

    public static WGPUBindGroupEntry Texture(uint slot, WgpuHandle<WGPUTextureView> view) => new() {
        Binding = slot,
        TextureView = (WGPUTextureView*)view.DangerousGetHandle()
    };

    public static WGPUBindGroupEntry Sampler(uint slot, Entity sampler) => new() {
        Binding = slot,
        Sampler = (WGPUSampler*)sampler.GetWgpu<WGPUSampler>().DangerousGetHandle()
    };

    public static Entity Layout(GpuResources owner, ReadOnlySpan<WGPUBindGroupLayoutEntry> entries)
    {
        fixed (WGPUBindGroupLayoutEntry* pointer = entries) {
            var descriptor = WGPUBindGroupLayoutDescriptor.Default;
            descriptor.EntryCount = (nuint)entries.Length;
            descriptor.Entries = pointer;
            return owner.Own(Wgpu.CreateBindGroupLayout(owner.Device, descriptor));
        }
    }

    public static Entity Group(GpuResources owner, Entity layout, ReadOnlySpan<WGPUBindGroupEntry> entries)
    {
        fixed (WGPUBindGroupEntry* pointer = entries) {
            var descriptor = WGPUBindGroupDescriptor.Default;
            descriptor.Layout = (WGPUBindGroupLayout*)layout.GetWgpu<WGPUBindGroupLayout>().DangerousGetHandle();
            descriptor.EntryCount = (nuint)entries.Length;
            descriptor.Entries = pointer;
            return owner.Own(Wgpu.CreateBindGroup(owner.Device, descriptor));
        }
    }

    public static Entity PipelineLayout(GpuResources owner, params ReadOnlySpan<Entity> layouts)
    {
        var pointers = stackalloc WGPUBindGroupLayout*[layouts.Length];
        for (var i = 0; i < layouts.Length; i++)
            pointers[i] = (WGPUBindGroupLayout*)layouts[i].GetWgpu<WGPUBindGroupLayout>().DangerousGetHandle();
        var descriptor = WGPUPipelineLayoutDescriptor.Default;
        descriptor.BindGroupLayoutCount = (nuint)layouts.Length;
        descriptor.BindGroupLayouts = pointers;
        return owner.Own(Wgpu.CreatePipelineLayout(owner.Device, descriptor));
    }
}
