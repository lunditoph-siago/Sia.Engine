using Sia;
using Sia.Graphics.Reactive;
using Sia.Math;
using Sia.RenderGraph;
using Sia.WebGPU;

namespace Sia.Engine.Rendering.Pbr;

public sealed partial class VisibilityPbrFeature
{
    private static CompactionGpu CreateCompactionGpu(World world, WgpuHandle<WGPUDevice> device, List<Entity> acquired)
    {
        var layout = Layout(world, device, [
            BufferLayout(0, WGPUBufferBindingType.Uniform, 32, WGPUShaderStage.Compute),
            BufferLayout(1, WGPUBufferBindingType.ReadOnlyStorage, 64, WGPUShaderStage.Compute),
            BufferLayout(2, WGPUBufferBindingType.Storage, 16, WGPUShaderStage.Compute),
            BufferLayout(3, WGPUBufferBindingType.Storage, 4, WGPUShaderStage.Compute),
            BufferLayout(4, WGPUBufferBindingType.Storage, 80, WGPUShaderStage.Compute)
        ], acquired);
        var shader = Own(world, Wgpu.CreateWgslShaderModule(device, PbrShaderSource.LoadVisibilityCompact(), "visibility-compact"), acquired);
        var pipelineLayout = PipelineLayout(world, device, [layout], acquired);
        return new(layout, ComputePipeline(world, device, shader, pipelineLayout, "local_prefix", acquired),
            ComputePipeline(world, device, shader, pipelineLayout, "scan", acquired),
            ComputePipeline(world, device, shader, pipelineLayout, "add_prefix", acquired),
            ComputePipeline(world, device, shader, pipelineLayout, "finish", acquired));
    }

    private static uint CompactionGroups(uint count) => (uint)System.Math.Max(1ul, ((ulong)count + 255) / 256);

    private CompactionViewGpu CreateCompactionView(LodGpu lod, Entity state, Entity heap, Entity indirect,
        WGPULimits limits, List<Entity> acquired)
    {
        var gpu = lod.Compaction;
        var levels = new List<(uint Offset, uint Count)> { (0, CompactionGroups(lod.Count)) };
        while (levels[^1].Count > 1) {
            var last = levels[^1];
            levels.Add((checked(last.Offset + last.Count), CompactionGroups(last.Count)));
        }
        var parameters = new List<Entity>();
        var main = Group(default, false);
        var post = Group(default, true);
        var middle = new List<CompactionStepGpu>();
        for (var i = 0; i < levels.Count - 1; i++) {
            var level = levels[i];
            middle.Add(new(gpu.Scan, Group(new uint4(level.Offset, level.Count, levels[i + 1].Offset, 0), false), CompactionGroups(level.Count)));
        }
        for (var i = levels.Count - 3; i >= 0; i--) {
            var level = levels[i];
            middle.Add(new(gpu.Add, Group(new uint4(level.Offset, level.Count, levels[i + 1].Offset, 0), false), CompactionGroups(level.Count)));
        }
        var groups = CompactionGroups(lod.Count);
        return new(parameters.ToArray(),
            [new(gpu.Local, main, groups), .. middle, new(gpu.Finish, main, groups)],
            [new(gpu.Local, post, groups), .. middle, new(gpu.Finish, post, groups)]);

        Entity Group(uint4 range, bool isPost)
        {
            range.w = levels[^1].Offset;
            var parameter = Upload<uint4>(_world, _device.GetWgpu<WGPUDevice>(), _queue.GetWgpu<WGPUQueue>(),
                [new uint4(lod.Count, (uint)_patchTree!.Nodes.Length, lod.DispatchDimension, isPost ? 1u : 0u), range],
                WGPUBufferUsage.Uniform, limits, acquired);
            parameters.Add(parameter);
            return Own(_world, BindGroup(gpu.Layout, [BufferEntry(0, parameter), BufferEntry(1, lod.Patches),
                BufferEntry(2, state), BufferEntry(3, heap), BufferEntry(4, indirect)]), acquired);
        }
    }

    private readonly record struct CompactionGpu(Entity Layout, Entity Local, Entity Scan, Entity Add, Entity Finish);
    private readonly record struct CompactionStepGpu(Entity Pipeline, Entity Group, uint Count);
    private readonly record struct CompactionViewGpu(Entity[] Parameters, CompactionStepGpu[] Main, CompactionStepGpu[] Post);

    private sealed partial class ViewState
    {
        public void DeclareCompact(RenderGraphPassDeclarationBuilder declaration)
        {
            declaration.Read(s_PatchKey, RenderGraphBufferUsage.Storage)
                .ReadWrite(s_LodStateKey, RenderGraphBufferUsage.Storage).ReadWrite(s_LodHeapKey, RenderGraphBufferUsage.Storage)
                .ReadWrite(s_IndirectKey, RenderGraphBufferUsage.Storage);
            for (var i = 0; i < Lod!.Value.Compaction.Parameters.Length; i++) {
                declaration.Read(new RenderGraphBufferKey("visibility-compact-params-" + i), RenderGraphBufferUsage.Uniform);
            }
        }

        public void CompactMain(WgpuReactiveRenderGraphPassContext context) => Compact(context, Lod!.Value.Compaction.Main);
        public void CompactPost(WgpuReactiveRenderGraphPassContext context) => Compact(context, Lod!.Value.Compaction.Post);

        private void Compact(WgpuReactiveRenderGraphPassContext context, CompactionStepGpu[] steps)
        {
            var dimension = Owner._gpuLod!.Value.DispatchDimension;
            for (var i = 0; i < steps.Length; i++) {
                var step = steps[i];
                var pass = BeginCompute(context, i == 0, i == steps.Length - 1);
                try {
                    Wgpu.SetComputePipeline(pass, step.Pipeline.GetWgpu<WGPUComputePipeline>());
                    Wgpu.SetBindGroup(pass, 0, step.Group.GetWgpu<WGPUBindGroup>());
                    Wgpu.DispatchWorkgroups(pass, System.Math.Min(step.Count, dimension), (step.Count + dimension - 1) / dimension);
                }
                finally { Wgpu.EndComputePass(pass); Wgpu.Release(ref pass); }
            }
        }
    }
}
