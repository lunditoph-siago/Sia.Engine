using Sia;
using Sia.Graphics.Reactive;
using Sia.Math;
using Sia.WebGPU;

namespace Sia.Engine.Rendering.Pbr;

public sealed partial class VisibilityPbrFeature
{
    private ShadowIndexGpu? _shadowIndices;

    private ShadowIndexGpu? CreateShadowIndices(List<Entity> acquired)
    {
        if (_gpuLod is null) { return null; }
        var device = _device.GetWgpu<WGPUDevice>();
        var limits = Wgpu.GetLimits(device);
        var size = System.Math.Max(1ul, ShadowTriangleCapacity) * 12;
        if (ShadowTriangleCapacity > (1ul << 24) || size > limits.MaxStorageBufferBindingSize || size > limits.MaxBufferSize
            || ((ulong)ShadowTriangleCapacity + 63) / 64 > (ulong)_gpuLod.Value.DispatchDimension * _gpuLod.Value.DispatchDimension) { return null; }
        var indices = Allocate(_world, device, size, WGPUBufferUsage.Storage | WGPUBufferUsage.Index, limits, acquired);
        var arguments = Allocate(_world, device, 32, WGPUBufferUsage.Storage | WGPUBufferUsage.Indirect, limits, acquired);
        var parameter = Upload<uint4>(_world, device, _queue.GetWgpu<WGPUQueue>(),
            [new(_gpuLod.Value.DispatchDimension, 0, 0, 0)], WGPUBufferUsage.Uniform, limits, acquired);
        var layout = Layout(_world, device, [BufferLayout(0, WGPUBufferBindingType.Uniform, 16, WGPUShaderStage.Compute),
            BufferLayout(1, WGPUBufferBindingType.ReadOnlyStorage, 8, WGPUShaderStage.Compute),
            BufferLayout(2, WGPUBufferBindingType.ReadOnlyStorage, 8, WGPUShaderStage.Compute),
            BufferLayout(3, WGPUBufferBindingType.ReadOnlyStorage, 80, WGPUShaderStage.Compute),
            BufferLayout(4, WGPUBufferBindingType.Storage, 12, WGPUShaderStage.Compute)], acquired);
        var argumentsLayout = Layout(_world, device, [BufferLayout(0, WGPUBufferBindingType.Storage, 32, WGPUShaderStage.Compute)], acquired);
        var argumentsGroup = Own(_world, BindGroup(argumentsLayout, [BufferEntry(0, arguments)]), acquired);
        var shader = Own(_world, Wgpu.CreateWgslShaderModule(device, PbrShaderSource.LoadVisibilityShadowIndices(), "visibility-shadow-indices"), acquired);
        var pipelineLayout = PipelineLayout(_world, device, [layout], acquired);
        var prepareLayout = PipelineLayout(_world, device, [layout, argumentsLayout], acquired);
        return new(indices, arguments, parameter, layout, argumentsGroup,
            ComputePipeline(_world, device, shader, prepareLayout, "prepare", acquired),
            ComputePipeline(_world, device, shader, pipelineLayout, "emit", acquired));
    }

    private Entity CreateShadowIndexGroup(ShadowIndexGpu gpu, ViewState view, List<Entity> acquired) =>
        Own(_world, BindGroup(gpu.Layout, [BufferEntry(0, gpu.Parameters), BufferEntry(1, view.WorkBuffer),
            BufferEntry(2, _geometry[2]), BufferEntry(3, view.Indirect), BufferEntry(4, gpu.Indices)]), acquired);

    private sealed record ShadowIndexGpu(Entity Indices, Entity Arguments, Entity Parameters, Entity Layout, Entity ArgumentsGroup, Entity Prepare, Entity Emit);

    private sealed partial class ViewState
    {
        private void EmitShadowIndices(WgpuReactiveRenderGraphPassContext context, Entity group)
        {
            var gpu = Owner._shadowIndices!;
            var pass = BeginCompute(context);
            try {
                Wgpu.SetBindGroup(pass, 0, group.GetWgpu<WGPUBindGroup>());
                Wgpu.SetBindGroup(pass, 1, gpu.ArgumentsGroup.GetWgpu<WGPUBindGroup>());
                Wgpu.SetComputePipeline(pass, gpu.Prepare.GetWgpu<WGPUComputePipeline>());
                Wgpu.DispatchWorkgroups(pass, 1);
                Wgpu.SetComputePipeline(pass, gpu.Emit.GetWgpu<WGPUComputePipeline>());
                Wgpu.DispatchWorkgroupsIndirect(pass, gpu.Arguments.GetWgpu<WGPUBuffer>());
            }
            finally { Wgpu.EndComputePass(pass); Wgpu.Release(ref pass); }
        }
    }
}
