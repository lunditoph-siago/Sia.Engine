using Sia;
using Sia.Graphics.Reactive;
using Sia.RenderGraph;
using Sia.WebGPU;

namespace Sia.Engine.Rendering.Pbr;

public sealed partial class VisibilityPbrFeature
{
    private static readonly string[] s_TimingStages = [
        "visibility-lod-project", "visibility-lod-select", "visibility-cull-main", "visibility-compact-main",
        "visibility-lod-emit", "visibility-raster", "visibility-hzb-main", "visibility-cull-post",
        "visibility-compact-post", "visibility-emit-post", "visibility-raster-post", "visibility-hzb-final",
        "visibility-material-tiles", "visibility-resolve", "visibility-output"
    ];

    public static ReadOnlySpan<string> GpuTimingStages => s_TimingStages;
    public RenderGraphBufferKey GpuStatisticsTarget => s_IndirectKey;
    public RenderGraphBufferKey GpuTimingsTarget { get; } = new("visibility-timings");

    private TimingGpu CreateTiming(WgpuHandle<WGPUDevice> device, WGPULimits limits, List<Entity> acquired) => new(
        Own(_world, Wgpu.CreateQuerySet(device, WGPUQueryType.Timestamp, (uint)s_TimingStages.Length * 2, "visibility-timings"), acquired),
        Allocate(_world, device, (ulong)s_TimingStages.Length * 16, WGPUBufferUsage.QueryResolve | WGPUBufferUsage.CopySrc | WGPUBufferUsage.CopyDst, limits, acquired));

    private readonly record struct TimingGpu(Entity Queries, Entity Results);

    private sealed partial class ViewState
    {
        public TimingGpu? Timing { get; init; }

        public void DeclareSurfaceTiming(RenderGraphPassDeclarationBuilder declaration) => declaration
            .Read(Owner.HdrTarget, RenderGraphTextureUsage.TextureBinding)
            .Write(Owner.GpuTimingsTarget, RenderGraphBufferUsage.QueryResolve | RenderGraphBufferUsage.CopyDestination);

        public unsafe void ResolveSurfaceTiming(WgpuReactiveRenderGraphPassContext context)
        {
            var buffer = context.GetBuffer(Owner.GpuTimingsTarget);
            Wgpu.ResolveQuerySet(context.CommandEncoder, Timing!.Value.Queries.GetWgpu<WGPUQuerySet>(),
                0, (uint)(s_TimingStages.Length - 1) * 2, buffer);
            WgpuUnsafe.wgpuCommandEncoderClearBuffer((WGPUCommandEncoder*)context.CommandEncoder.DangerousGetHandle(),
                (WGPUBuffer*)buffer.DangerousGetHandle(), (ulong)(s_TimingStages.Length - 1) * 16, 16);
        }

        private uint TimingIndex(WgpuReactiveRenderGraphPassContext context)
        {
            var stage = Array.IndexOf(s_TimingStages, context.Pass.Name);
            if (stage < 0) { throw new InvalidOperationException("Unknown visibility timing stage."); }
            return (uint)stage * 2;
        }

        private unsafe WgpuHandle<WGPUComputePassEncoder> BeginCompute(WgpuReactiveRenderGraphPassContext context)
        {
            if (Timing is not { } timing) { return context.GetOrBeginComputePass(); }
            var index = TimingIndex(context);
            var timestamps = new WGPUPassTimestampWrites {
                QuerySet = (WGPUQuerySet*)timing.Queries.GetWgpu<WGPUQuerySet>().DangerousGetHandle(),
                BeginningOfPassWriteIndex = index,
                EndOfPassWriteIndex = index + 1
            };
            var descriptor = WGPUComputePassDescriptor.Default;
            descriptor.TimestampWrites = &timestamps;
            return Wgpu.BeginComputePass(context.CommandEncoder, descriptor);
        }

        private unsafe WgpuHandle<WGPURenderPassEncoder> BeginTimedRender(WgpuReactiveRenderGraphPassContext context,
            RenderGraphTextureKey target, WGPULoadOp load, bool depth)
        {
            var index = TimingIndex(context);
            var timestamps = new WGPUPassTimestampWrites {
                QuerySet = (WGPUQuerySet*)Timing!.Value.Queries.GetWgpu<WGPUQuerySet>().DangerousGetHandle(),
                BeginningOfPassWriteIndex = index, EndOfPassWriteIndex = index + 1
            };
            var color = WGPURenderPassColorAttachment.Default;
            color.View = (WGPUTextureView*)context.GetTextureView(target, depth || Frame.ColorCacheable).DangerousGetHandle();
            color.LoadOp = load;
            color.StoreOp = WGPUStoreOp.Store;
            var depthAttachment = WGPURenderPassDepthStencilAttachment.Default;
            if (depth) {
                depthAttachment.View = (WGPUTextureView*)context.GetTextureView(Frame.DepthTarget).DangerousGetHandle();
                depthAttachment.DepthLoadOp = load;
                depthAttachment.DepthStoreOp = WGPUStoreOp.Store;
                depthAttachment.DepthClearValue = 1;
            }
            var descriptor = WGPURenderPassDescriptor.Default;
            descriptor.ColorAttachmentCount = 1;
            descriptor.ColorAttachments = &color;
            descriptor.DepthStencilAttachment = depth ? &depthAttachment : null;
            descriptor.TimestampWrites = &timestamps;
            return Wgpu.BeginRenderPass(context.CommandEncoder, descriptor);
        }

        private void TimedRaster(WgpuReactiveRenderGraphPassContext context, bool post)
        {
            var pass = BeginTimedRender(context, Owner.VisibilityTarget, post ? WGPULoadOp.Load : WGPULoadOp.Clear, true);
            try {
                Wgpu.SetRenderPipeline(pass, Owner._raster.GetWgpu<WGPURenderPipeline>());
                Wgpu.SetBindGroup(pass, 0, Group.GetWgpu<WGPUBindGroup>());
                Wgpu.DrawIndirect(pass, Indirect.GetWgpu<WGPUBuffer>(), post ? 32ul : 0ul);
            }
            finally { Wgpu.EndRenderPass(pass); Wgpu.Release(ref pass); }
        }

        private void TimedOutput(WgpuReactiveRenderGraphPassContext context)
        {
            var pass = BeginTimedRender(context, Frame.ColorTarget, Frame.ColorLoadOp, false);
            try {
                Wgpu.SetRenderPipeline(pass, Owner._output.Pipeline.GetWgpu<WGPURenderPipeline>());
                Wgpu.SetBindGroup(pass, 0, _outputGroup.GetWgpu<WGPUBindGroup>());
                Wgpu.Draw(pass, 3);
            }
            finally { Wgpu.EndRenderPass(pass); Wgpu.Release(ref pass); }
            Wgpu.ResolveQuerySet(context.CommandEncoder, Timing!.Value.Queries.GetWgpu<WGPUQuerySet>(),
                0, (uint)s_TimingStages.Length * 2, context.GetBuffer(Owner.GpuTimingsTarget));
        }
    }
}
