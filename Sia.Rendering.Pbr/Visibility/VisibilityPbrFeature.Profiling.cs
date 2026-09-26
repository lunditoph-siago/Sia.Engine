using Sia;
using Sia.Graphics.Reactive;
using Sia.RenderGraph;
using Sia.WebGPU;

namespace Sia.Engine.Rendering.Pbr;

public sealed partial class VisibilityPbrFeature
{
    private static readonly string[] s_TimingStages = [
        "visibility-lod-select", "visibility-hzb-main", "visibility-raster",
        "visibility-material-tiles", "visibility-resolve", "visibility-output"
    ];

    public static ReadOnlySpan<string> GpuTimingStages => s_TimingStages;
    public RenderGraphBufferKey GpuStatisticsTarget => s_IndirectKey;
    public RenderGraphBufferKey GpuTimingsTarget { get; } = new("visibility-timings");

    private TimingGpu CreateTiming(WgpuHandle<WGPUDevice> device, WGPULimits limits, List<Entity> acquired) => new(
        Own(_world, Wgpu.CreateQuerySet(device, WGPUQueryType.Timestamp, (uint)s_TimingStages.Length * 2, "visibility-timings"), acquired),
        Allocate(_world, device, (ulong)s_TimingStages.Length * 16, WGPUBufferUsage.QueryResolve | WGPUBufferUsage.CopySrc | WGPUBufferUsage.CopyDst, limits, acquired),
        Allocate(_world, device, (ulong)s_TimingStages.Length * 256, WGPUBufferUsage.QueryResolve | WGPUBufferUsage.CopySrc, limits, acquired));

    private readonly record struct TimingGpu(Entity Queries, Entity Results, Entity Scratch);

    private sealed partial class ViewState
    {
        public TimingGpu? Timing { get; init; }
        public uint TimingWritten { get; set; }

        private unsafe void ResolveTiming(WgpuReactiveRenderGraphPassContext context)
        {
            if (Timing is not { } timing) return;
            var target = context.GetBuffer(Owner.GpuTimingsTarget);
            // Resolve only queries actually written this frame. Each sparse resolve
            // has the required 256-byte alignment; pack the public results by copy.
            WgpuUnsafe.wgpuCommandEncoderClearBuffer((WGPUCommandEncoder*)context.CommandEncoder.DangerousGetHandle(),
                (WGPUBuffer*)target.DangerousGetHandle(), 0, (ulong)s_TimingStages.Length * 16);
            for (var stage = 0; stage < s_TimingStages.Length; stage++) {
                if ((TimingWritten & (1u << stage)) == 0) continue;
                Wgpu.ResolveQuerySet(context.CommandEncoder, timing.Queries.GetWgpu<WGPUQuerySet>(),
                    (uint)stage * 2, 2, timing.Scratch.GetWgpu<WGPUBuffer>(), (ulong)stage * 256);
                Wgpu.CopyBufferToBuffer(context.CommandEncoder, timing.Scratch.GetWgpu<WGPUBuffer>(), (ulong)stage * 256,
                    target, (ulong)stage * 16, 16);
            }
        }

        public void DeclareSurfaceTiming(RenderGraphPassDeclarationBuilder declaration) => declaration
            .Read(Owner.HdrTarget, RenderGraphTextureUsage.TextureBinding)
            .Write(Owner.GpuTimingsTarget, RenderGraphBufferUsage.QueryResolve | RenderGraphBufferUsage.CopyDestination);

        public void ResolveSurfaceTiming(WgpuReactiveRenderGraphPassContext context) => ResolveTiming(context);

        private static bool IsCheckpoint(WgpuReactiveRenderGraphPassContext context) => Array.IndexOf(s_TimingStages, context.Pass.Name) >= 0;

        private uint TimingIndex(WgpuReactiveRenderGraphPassContext context)
        {
            var stage = Array.IndexOf(s_TimingStages, context.Pass.Name);
            if (stage < 0) { throw new InvalidOperationException("Unknown visibility timing stage."); }
            TimingWritten |= 1u << stage;
            return (uint)stage * 2;
        }

        private unsafe WgpuHandle<WGPUComputePassEncoder> BeginCompute(WgpuReactiveRenderGraphPassContext context)
        {
            if (Timing is not { } timing || !IsCheckpoint(context)) { return context.GetOrBeginComputePass(); }
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
                if (Owner._fixedGeometry is not null) {
                    Wgpu.SetIndexBuffer(pass, Clusters!.Value.Indices.GetWgpu<WGPUBuffer>(), WGPUIndexFormat.Uint32);
                    Wgpu.DrawIndexedIndirect(pass, Indirect.GetWgpu<WGPUBuffer>());
                } else Wgpu.DrawIndirect(pass, Indirect.GetWgpu<WGPUBuffer>(), post ? 32ul : 0ul);
            }
            finally { Wgpu.EndRenderPass(pass); Wgpu.Release(ref pass); }
        }

        private unsafe void TimedOutput(WgpuReactiveRenderGraphPassContext context)
        {
            var pass = BeginTimedRender(context, Frame.ColorTarget, Frame.ColorLoadOp, false);
            try {
                Wgpu.SetRenderPipeline(pass, Owner._output.Pipeline.GetWgpu<WGPURenderPipeline>());
                Wgpu.SetBindGroup(pass, 0, _outputGroup.GetWgpu<WGPUBindGroup>());
                Wgpu.Draw(pass, 3);
            }
            finally { Wgpu.EndRenderPass(pass); Wgpu.Release(ref pass); }
            ResolveTiming(context);
        }
    }
}
