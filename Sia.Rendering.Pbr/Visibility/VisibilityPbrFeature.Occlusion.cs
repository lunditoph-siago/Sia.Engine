using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Sia;
using Sia.Graphics.Reactive;
using Sia.Math;
using Sia.RenderGraph;
using Sia.WebGPU;

namespace Sia.Engine.Rendering.Pbr;

public sealed partial class VisibilityPbrFeature
{
    private static readonly RenderGraphBufferKey s_HzbKey = new("visibility-hzb");
    private static readonly RenderGraphBufferKey s_HzbParamsKey = new("visibility-hzb-params");

    private static OcclusionGpu CreateOcclusionGpu(World world, WgpuHandle<WGPUDevice> device, Entity lodLayout, Entity lodPipelineLayout,
        Entity lodShader, List<Entity> acquired)
    {
        var layout = Layout(world, device, [
            BufferLayout(0, WGPUBufferBindingType.Uniform, 592, WGPUShaderStage.Compute),
            BufferLayout(1, WGPUBufferBindingType.ReadOnlyStorage, 4, WGPUShaderStage.Compute)
        ], acquired);
        var pipelineLayout = PipelineLayout(world, device, [lodLayout, layout], acquired);
        var reduceLayout = Layout(world, device, [
            TextureLayout(0, WGPUTextureSampleType.Depth, WGPUShaderStage.Compute),
            BufferLayout(1, WGPUBufferBindingType.Storage, 4, WGPUShaderStage.Compute),
            BufferLayout(2, WGPUBufferBindingType.Uniform, 32, WGPUShaderStage.Compute)
        ], acquired);
        var reducePipelineLayout = PipelineLayout(world, device, [reduceLayout], acquired);
        var shader = Own(world, Wgpu.CreateWgslShaderModule(device, PbrShaderSource.LoadVisibilityHzb(), "visibility-hzb"), acquired);
        return new(layout, reduceLayout,
            ComputePipeline(world, device, lodShader, pipelineLayout, "cull_main", acquired),
            ComputePipeline(world, device, lodShader, pipelineLayout, "cull_post", acquired),
            ComputePipeline(world, device, lodShader, lodPipelineLayout, "emit_post", acquired),
            ComputePipeline(world, device, shader, reducePipelineLayout, "seed", acquired),
            ComputePipeline(world, device, shader, reducePipelineLayout, "reduce", acquired));
    }

    private void PrepareOcclusion(ViewState view, float4x4 projection)
    {
        if (view.Hzb is not { } previous || previous.Width != view.Width || previous.Height != view.Height) {
            var next = CreateHzb(view.Width, view.Height);
            view.ReleaseHzbGroups();
            if (view.Hzb is { } old) {
                for (var i = old.Owned.Length - 1; i >= 0; i--) { old.Owned[i].Destroy(); }
            }
            view.Hzb = next;
            view.HistoryValid = false;
        }
        view.Projection = projection;
        var hzb = view.Hzb!.Value;
        var parameters = new HzbParamsGpu(view.PreviousProjection,
            new uint4(view.Width, view.Height, hzb.Factor, view.HistoryValid ? 1u : 0u), hzb.Levels);
        Wgpu.WriteBuffer<HzbParamsGpu>(_queue.GetWgpu<WGPUQueue>(), hzb.Parameters.GetWgpu<WGPUBuffer>(), 0, [parameters]);
    }

    private HzbViewGpu CreateHzb(uint width, uint height)
    {
        var device = _device.GetWgpu<WGPUDevice>();
        var limits = Wgpu.GetLimits(device);
        var factor = 4u;
        HzbLevelsGpu levels;
        int count;
        ulong size;
        do {
            levels = default;
            count = 0;
            size = 0;
            var w = (width + factor - 1) / factor;
            var h = (height + factor - 1) / factor;
            while (true) {
                levels[count++] = new uint4(w, h, checked((uint)(size / 4)), 0);
                size += (ulong)w * h * 4;
                if (w == 1 && h == 1) { break; }
                w = (w + 1) / 2; h = (h + 1) / 2;
            }
            if (size <= limits.MaxBufferSize && size <= limits.MaxStorageBufferBindingSize) { break; }
            factor = checked(factor * 2);
        } while (true);
        var acquired = new List<Entity>();
        try {
            var buffer = Allocate(_world, device, size, WGPUBufferUsage.Storage | WGPUBufferUsage.CopySrc, limits, acquired);
            var parameters = Upload<HzbParamsGpu>(_world, device, _queue.GetWgpu<WGPUQueue>(), [default],
                WGPUBufferUsage.Uniform, limits, acquired);
            var group = Own(_world, BindGroup(_gpuLod!.Value.Occlusion.Layout,
                [BufferEntry(0, parameters), BufferEntry(1, buffer)]), acquired);
            var reduceParameters = new Entity[count];
            for (var i = 0; i < count; i++) {
                var source = i == 0 ? new uint4(width, height, 0, factor) : levels[i - 1] with { w = 2 };
                reduceParameters[i] = Upload<uint4>(_world, device, _queue.GetWgpu<WGPUQueue>(), [source, levels[i]],
                    WGPUBufferUsage.Uniform, limits, acquired);
            }
            return new(buffer, parameters, group, reduceParameters, acquired.ToArray(), levels, width, height, factor);
        }
        catch {
            for (var i = acquired.Count - 1; i >= 0; i--) { acquired[i].Destroy(); }
            throw;
        }
    }

    private static void BuildMainOcclusionGraph(ref RenderGraphBuildContext graph, ViewState view)
    {
        var hzb = view.Hzb!.Value;
        ImportBuffer(ref graph, s_HzbKey, hzb.Buffer, RenderGraphBufferUsage.Storage | RenderGraphBufferUsage.CopySource);
        ImportBuffer(ref graph, s_HzbParamsKey, hzb.Parameters, RenderGraphBufferUsage.Uniform);
        var parameters = view.Lod!.Value.Compaction.Parameters;
        for (var i = 0; i < parameters.Length; i++) {
            ImportBuffer(ref graph, new("visibility-compact-params-" + i), parameters[i], RenderGraphBufferUsage.Uniform);
        }
        graph.UseComputePass(new("visibility-cull-main"), "visibility-cull-main", DeclareCull, view.CullMain);
        graph.UseComputePass(new("visibility-compact-main"), "visibility-compact-main", view.DeclareCompact, view.CompactMain);
    }

    private static void DeclareCull(RenderGraphPassDeclarationBuilder declaration) => declaration
        .Read(s_CameraKey, RenderGraphBufferUsage.Uniform).Read(s_LodParamsKey, RenderGraphBufferUsage.Uniform)
        .Read(s_HzbParamsKey, RenderGraphBufferUsage.Uniform).Read(s_HzbKey, RenderGraphBufferUsage.Storage)
        .Read(s_PatchKey, RenderGraphBufferUsage.Storage).Read(s_GeometryKeys[4], RenderGraphBufferUsage.Storage)
        .Read(s_LodDispatchKey, RenderGraphBufferUsage.Indirect).Read(s_IndirectKey, RenderGraphBufferUsage.Storage)
        .ReadWrite(s_LodStateKey, RenderGraphBufferUsage.Storage);

    private static void BuildPostOcclusionGraph(ref RenderGraphBuildContext graph, ViewState view)
    {
        var hzb = view.Hzb!.Value;
        for (var i = 0; i < hzb.ReduceParameters.Length; i++) {
            ImportBuffer(ref graph, new("visibility-hzb-level-" + i), hzb.ReduceParameters[i], RenderGraphBufferUsage.Uniform);
        }
        graph.UseComputePass(new("visibility-hzb-main"), "visibility-hzb-main", view.DeclareHzb, view.BuildHzb);
        graph.UseComputePass(new("visibility-cull-post"), "visibility-cull-post", DeclareCull, view.CullPost);
        graph.UseComputePass(new("visibility-compact-post"), "visibility-compact-post", view.DeclareCompact, view.CompactPost);
        graph.UseComputePass(new("visibility-emit-post"), "visibility-emit-post", declaration => declaration
            .Read(s_LodParamsKey, RenderGraphBufferUsage.Uniform).Read(s_PatchKey, RenderGraphBufferUsage.Storage)
            .Read(s_LodStateKey, RenderGraphBufferUsage.Storage).Read(s_IndirectKey, RenderGraphBufferUsage.Storage)
            .Read(s_LodDispatchKey, RenderGraphBufferUsage.Indirect)
            .ReadWrite(s_WorkKey, RenderGraphBufferUsage.Storage), view.EmitPost);
        graph.UsePass(new("visibility-raster-post"), "visibility-raster-post", view.DeclarePostRaster, view.PostRaster);
        graph.UseComputePass(new("visibility-hzb-final"), "visibility-hzb-final", view.DeclareHzb, view.BuildFinalHzb);
        graph.ExportBuffer(s_HzbKey, RenderGraphBufferUsage.Storage);
    }

    private sealed partial class ViewState
    {
        public HzbViewGpu? Hzb { get; set; }
        public bool HistoryValid { get; set; }
        public float4x4 PreviousProjection { get; set; }
        public float4x4 Projection { get; set; }
        private Entity[] _hzbGroups = [];
        private WgpuHandle<WGPUTextureView> _hzbDepth;

        public void CullMain(WgpuReactiveRenderGraphPassContext context) =>
            DispatchLod(context, Owner._gpuLod!.Value.Occlusion.CullMain, 0, Hzb!.Value.Group, 0);

        public void CullPost(WgpuReactiveRenderGraphPassContext context) =>
            DispatchLod(context, Owner._gpuLod!.Value.Occlusion.CullPost, 0, Hzb!.Value.Group, 0);

        public void EmitPost(WgpuReactiveRenderGraphPassContext context) =>
            DispatchLod(context, Owner._gpuLod!.Value.Occlusion.EmitPost, 0, indirectOffset: 12);

        public void DeclarePostRaster(RenderGraphPassDeclarationBuilder declaration)
        {
            ReadGeometry(declaration);
            declaration.Read(s_IndirectKey, RenderGraphBufferUsage.Indirect)
                .ReadWrite(Owner.VisibilityTarget, RenderGraphTextureUsage.RenderAttachment)
                .ReadWrite(Frame.DepthTarget, RenderGraphTextureUsage.RenderAttachment);
        }

        public void PostRaster(WgpuReactiveRenderGraphPassContext context) => Raster(context, true);

        public void DeclareHzb(RenderGraphPassDeclarationBuilder declaration)
        {
            declaration.Read(Frame.DepthTarget, RenderGraphTextureUsage.TextureBinding).ReadWrite(s_HzbKey, RenderGraphBufferUsage.Storage);
            for (var i = 0; i < Hzb!.Value.ReduceParameters.Length; i++) {
                declaration.Read(new RenderGraphBufferKey("visibility-hzb-level-" + i), RenderGraphBufferUsage.Uniform);
            }
        }

        public void ReleaseHzbGroups()
        {
            foreach (var group in _hzbGroups) { group.Destroy(); }
            _hzbGroups = [];
            _hzbDepth = default;
        }

        public void BuildHzb(WgpuReactiveRenderGraphPassContext context)
        {
            var hzb = Hzb!.Value;
            var gpu = Owner._gpuLod!.Value.Occlusion;
            var depth = context.GetTextureView(Frame.DepthTarget);
            if (_hzbGroups.Length == 0 || _hzbDepth != depth) {
                var acquired = new List<Entity>();
                try {
                    foreach (var level in hzb.ReduceParameters) {
                        Own(Owner._world, Owner.BindGroup(gpu.ReduceLayout,
                            [TextureEntry(0, depth), BufferEntry(1, hzb.Buffer), BufferEntry(2, level)]), acquired);
                    }
                }
                catch {
                    for (var i = acquired.Count - 1; i >= 0; i--) { acquired[i].Destroy(); }
                    throw;
                }
                ReleaseHzbGroups();
                _hzbGroups = acquired.ToArray();
                _hzbDepth = depth;
            }
            var pass = BeginCompute(context);
            try {
                for (var level = 0; level < _hzbGroups.Length; level++) {
                    Wgpu.SetComputePipeline(pass, (level == 0 ? gpu.Seed : gpu.Reduce).GetWgpu<WGPUComputePipeline>());
                    Wgpu.SetBindGroup(pass, 0, _hzbGroups[level].GetWgpu<WGPUBindGroup>());
                    Wgpu.DispatchWorkgroups(pass, (hzb.Levels[level].x + 7) / 8, (hzb.Levels[level].y + 7) / 8);
                }
            }
            finally { Wgpu.EndComputePass(pass); Wgpu.Release(ref pass); }
        }

        public void BuildFinalHzb(WgpuReactiveRenderGraphPassContext context)
        {
            BuildHzb(context);
            PreviousProjection = Projection;
            HistoryValid = true;
        }
    }

    [InlineArray(32)]
    private struct HzbLevelsGpu { private uint4 _element; }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct HzbParamsGpu(float4x4 PreviousProjection, uint4 Size, HzbLevelsGpu Levels);

    private readonly record struct HzbViewGpu(Entity Buffer, Entity Parameters, Entity Group, Entity[] ReduceParameters,
        Entity[] Owned, HzbLevelsGpu Levels, uint Width, uint Height, uint Factor);

    private readonly record struct OcclusionGpu(Entity Layout, Entity ReduceLayout, Entity CullMain, Entity CullPost,
        Entity EmitPost, Entity Seed, Entity Reduce);
}
