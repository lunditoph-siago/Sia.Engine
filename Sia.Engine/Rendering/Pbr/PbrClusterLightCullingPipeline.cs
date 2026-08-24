using Sia.Engine.Camera;
using Sia.Engine.Lighting;
using Sia.Engine.Mesh;
using Sia;
using Sia.WebGPU;

namespace Sia.Engine.Rendering.Pbr;

internal static unsafe class ClusterCullingBindGroupLayout
{
    public const uint ConfigBinding = 0;
    public const uint LightsBinding = 1;
    public const uint LightGridBinding = 2;
    public const uint LightIndexListBinding = 3;
    private const int EntryCount = 4;

    public static WgpuHandle<WGPUBindGroupLayout> Create(WgpuHandle<WGPUDevice> device)
    {
        Span<WGPUBindGroupLayoutEntry> entries = stackalloc WGPUBindGroupLayoutEntry[EntryCount];
        entries[0] = WGPUBindGroupLayoutEntry.Default;
        entries[0].Binding = ConfigBinding;
        entries[0].Visibility = WGPUShaderStage.Compute;
        entries[0].Buffer = WGPUBufferBindingLayout.Default;
        entries[0].Buffer.Type = WGPUBufferBindingType.Uniform;

        entries[1] = WGPUBindGroupLayoutEntry.Default;
        entries[1].Binding = LightsBinding;
        entries[1].Visibility = WGPUShaderStage.Compute;
        entries[1].Buffer = WGPUBufferBindingLayout.Default;
        entries[1].Buffer.Type = WGPUBufferBindingType.ReadOnlyStorage;

        entries[2] = WGPUBindGroupLayoutEntry.Default;
        entries[2].Binding = LightGridBinding;
        entries[2].Visibility = WGPUShaderStage.Compute;
        entries[2].Buffer = WGPUBufferBindingLayout.Default;
        entries[2].Buffer.Type = WGPUBufferBindingType.Storage;

        entries[3] = WGPUBindGroupLayoutEntry.Default;
        entries[3].Binding = LightIndexListBinding;
        entries[3].Visibility = WGPUShaderStage.Compute;
        entries[3].Buffer = WGPUBufferBindingLayout.Default;
        entries[3].Buffer.Type = WGPUBufferBindingType.Storage;

        fixed (WGPUBindGroupLayoutEntry* entriesPtr = entries) {
            var descriptor = WGPUBindGroupLayoutDescriptor.Default;
            descriptor.EntryCount = EntryCount;
            descriptor.Entries = entriesPtr;
            return Wgpu.CreateBindGroupLayout(device, in descriptor);
        }
    }

    public static WgpuHandle<WGPUPipelineLayout> CreatePipelineLayout(
        WgpuHandle<WGPUDevice> device,
        WgpuHandle<WGPUBindGroupLayout> layout)
    {
        var layoutPtr = (WGPUBindGroupLayout*)layout.DangerousGetHandle();
        var descriptor = WGPUPipelineLayoutDescriptor.Default;
        descriptor.BindGroupLayoutCount = 1;
        descriptor.BindGroupLayouts = &layoutPtr;
        return Wgpu.CreatePipelineLayout(device, in descriptor);
    }

    public static WgpuHandle<WGPUBindGroup> CreateBindGroup(
        WgpuHandle<WGPUDevice> device,
        WgpuHandle<WGPUBindGroupLayout> layout,
        WgpuHandle<WGPUBuffer> config,
        WgpuHandle<WGPUBuffer> lights,
        ulong lightsSize,
        WgpuHandle<WGPUBuffer> lightGrid,
        ulong lightGridSize,
        WgpuHandle<WGPUBuffer> lightIndexList,
        ulong lightIndexListSize)
    {
        Span<WGPUBindGroupEntry> entries = stackalloc WGPUBindGroupEntry[EntryCount];
        entries[0] = WGPUBindGroupEntry.Default with {
            Binding = ConfigBinding,
            Buffer = (WGPUBuffer*)config.DangerousGetHandle(),
            Size = ClusterConfigGpu.Stride
        };
        entries[1] = WGPUBindGroupEntry.Default with {
            Binding = LightsBinding,
            Buffer = (WGPUBuffer*)lights.DangerousGetHandle(),
            Size = lightsSize
        };
        entries[2] = WGPUBindGroupEntry.Default with {
            Binding = LightGridBinding,
            Buffer = (WGPUBuffer*)lightGrid.DangerousGetHandle(),
            Size = lightGridSize
        };
        entries[3] = WGPUBindGroupEntry.Default with {
            Binding = LightIndexListBinding,
            Buffer = (WGPUBuffer*)lightIndexList.DangerousGetHandle(),
            Size = lightIndexListSize
        };
        fixed (WGPUBindGroupEntry* entriesPtr = entries) {
            var descriptor = WGPUBindGroupDescriptor.Default;
            descriptor.Layout = (WGPUBindGroupLayout*)layout.DangerousGetHandle();
            descriptor.EntryCount = EntryCount;
            descriptor.Entries = entriesPtr;
            return Wgpu.CreateBindGroup(device, in descriptor);
        }
    }
}

public sealed unsafe class PbrClusterLightCullingPipeline
{
    internal Entity ComputePipeline { get; }
    internal Entity BindGroupLayout { get; }

    private PbrClusterLightCullingPipeline(Entity computePipeline, Entity bindGroupLayout)
    {
        ComputePipeline = computePipeline;
        BindGroupLayout = bindGroupLayout;
    }

    public static PbrClusterLightCullingPipeline Create(World world, Entity device)
    {
        var deviceHandle = device.GetWgpu<WGPUDevice>();
        var shaderModule = world.OwnWgpu(Wgpu.CreateWgslShaderModule(
            deviceHandle, PbrShaderSource.LoadClusterLightCulling(), "cluster_light_culling"));
        var bindGroupLayout = world.OwnWgpu(ClusterCullingBindGroupLayout.Create(deviceHandle));
        var pipelineLayout = world.OwnWgpu(ClusterCullingBindGroupLayout.CreatePipelineLayout(
            deviceHandle, bindGroupLayout.GetWgpu<WGPUBindGroupLayout>()));
        var computePipeline = world.OwnWgpu(CreateComputePipeline(
            deviceHandle,
            shaderModule.GetWgpu<WGPUShaderModule>(),
            pipelineLayout.GetWgpu<WGPUPipelineLayout>()));
        return new PbrClusterLightCullingPipeline(computePipeline, bindGroupLayout);
    }

    private static WgpuHandle<WGPUComputePipeline> CreateComputePipeline(
        WgpuHandle<WGPUDevice> device,
        WgpuHandle<WGPUShaderModule> shaderModule,
        WgpuHandle<WGPUPipelineLayout> pipelineLayout)
    {
        var entryPoint = "cull"u8;
        fixed (byte* entry = entryPoint) {
            var descriptor = WGPUComputePipelineDescriptor.Default;
            descriptor.Layout = (WGPUPipelineLayout*)pipelineLayout.DangerousGetHandle();
            descriptor.Compute.Module = (WGPUShaderModule*)shaderModule.DangerousGetHandle();
            descriptor.Compute.EntryPoint = new WGPUStringView {
                Data = entry,
                Length = (nuint)entryPoint.Length
            };
            return Wgpu.CreateComputePipeline(device, in descriptor);
        }
    }
}
