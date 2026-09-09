using Sia;
using Sia.Graphics.Reactive;
using Sia.Math;
using Sia.WebGPU;

namespace Sia.Engine.Rendering.Pbr;

public sealed partial class VisibilityPbrFeature
{
    private static ParallelLodGpu CreateParallelLod(World world, WgpuHandle<WGPUDevice> device, Entity shader,
        Entity geometryLayout, uint capacity, uint passes, List<Entity> acquired)
    {
        var layout = Layout(world, device, [BufferLayout(0, WGPUBufferBindingType.Uniform, 32, WGPUShaderStage.Compute),
            BufferLayout(1, WGPUBufferBindingType.Storage, 4, WGPUShaderStage.Compute),
            BufferLayout(2, WGPUBufferBindingType.Storage, 72, WGPUShaderStage.Compute)], acquired);
        var pipelineLayout = PipelineLayout(world, device, [geometryLayout, layout], acquired);
        return ConfigureParallelLod(new(layout, new[] { "parallel_init", "parallel_local", "parallel_scan", "parallel_add", "parallel_refine", "parallel_advance" }
            .Select(name => ComputePipeline(world, device, shader, pipelineLayout, name, acquired)).ToArray(), [], passes, 0), capacity, passes);
    }

    private static ParallelLodGpu ConfigureParallelLod(ParallelLodGpu gpu, uint capacity, uint passes)
    {
        var levels = new List<uint4>();
        var offset = checked(4u + capacity);
        var count = ParallelGroups(capacity);
        for (uint level = 1; count > 1; level++) {
            var next = checked(offset + count);
            levels.Add(new(offset, count, next, level));
            offset = next;
            count = ParallelGroups(count);
        }
        return gpu with { Levels = levels.ToArray(), Rounds = passes, ScratchBytes = checked(((ulong)offset + count) * 32) };
    }

    private static uint ParallelGroups(uint count) => (uint)System.Math.Max(1ul, ((ulong)count + 127) / 128);

    private ParallelLodViewGpu CreateParallelLodView(ParallelLodGpu gpu, Entity dispatch, uint capacity,
        WGPULimits limits, List<Entity> acquired)
    {
        var maximum = Allocate(_world, _device.GetWgpu<WGPUDevice>(), 4, WGPUBufferUsage.Storage, limits, acquired);
        var scans = gpu.Levels.Select(level => new ParallelLodStep(Group(level), ParallelGroups(level.y))).ToArray();
        var adds = gpu.Levels.Take(System.Math.Max(0, gpu.Levels.Length - 1)).Reverse()
            .Select(level => new ParallelLodStep(Group(level), ParallelGroups(level.y))).ToArray();
        return new(Group(default), scans, adds);

        Entity Group(uint4 range)
        {
            var parameter = Upload<uint4>(_world, _device.GetWgpu<WGPUDevice>(), _queue.GetWgpu<WGPUQueue>(),
                [range, new(capacity, gpu.Rounds, 0, 0)], WGPUBufferUsage.Uniform, limits, acquired);
            return Own(_world, BindGroup(gpu.Layout, [BufferEntry(0, parameter), BufferEntry(1, maximum), BufferEntry(2, dispatch)]), acquired);
        }
    }

    private sealed record ParallelLodGpu(Entity Layout, Entity[] Pipelines, uint4[] Levels, uint Rounds, ulong ScratchBytes);
    private sealed record ParallelLodViewGpu(Entity Group, ParallelLodStep[] Scans, ParallelLodStep[] Adds);
    private readonly record struct ParallelLodStep(Entity Group, uint Count);

    private sealed partial class ViewState
    {
        private void SelectParallelLod(WgpuReactiveRenderGraphPassContext context, ParallelLodViewGpu view)
        {
            var lod = LodConfiguration!.Value;
            var gpu = lod.Parallel!;
            var pass = BeginCompute(context);
            try {
                Wgpu.SetBindGroup(pass, 0, Lod!.Value.Group.GetWgpu<WGPUBindGroup>());
                Run(0, view.Group, 1);
                for (var round = 0u; round < gpu.Rounds; round++) {
                    Run(1, view.Group, ParallelGroups(lod.Capacity));
                    foreach (var scan in view.Scans) { Run(2, scan.Group, scan.Count); }
                    foreach (var add in view.Adds) { Run(3, add.Group, add.Count); }
                    Run(4, view.Group, ParallelGroups(lod.Capacity));
                    Run(5, view.Group, 1);
                }
            }
            finally { Wgpu.EndComputePass(pass); Wgpu.Release(ref pass); }

            void Run(int pipeline, Entity group, uint count)
            {
                Wgpu.SetComputePipeline(pass, gpu.Pipelines[pipeline].GetWgpu<WGPUComputePipeline>());
                Wgpu.SetBindGroup(pass, 1, group.GetWgpu<WGPUBindGroup>());
                Wgpu.DispatchWorkgroups(pass, System.Math.Min(count, lod.DispatchDimension), (count + lod.DispatchDimension - 1) / lod.DispatchDimension);
            }
        }
    }
}
