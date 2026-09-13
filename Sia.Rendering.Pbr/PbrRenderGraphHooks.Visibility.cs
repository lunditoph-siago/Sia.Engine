using Sia.Graphics.Reactive;
using Sia.RenderGraph;
using Sia.WebGPU;

namespace Sia.Engine.Rendering.Pbr;

public static partial class PbrRenderGraphHooks
{
    internal static void UseTransparencyPass(ref RenderGraphBuildContext graph, PbrTransparentScene.View view,
        PbrViewState lighting, RenderGraphTextureKey color, RenderGraphTextureKey depth, bool enabled)
    {
        var state = graph.UseState(static () => new TransparencyState());
        state.View = view; state.Lighting = lighting; state.Color = color; state.Depth = depth;
        state.Enabled = enabled;
        if (enabled && view.Owner.HasTransmission) {
            graph.UseTexture(TransparencyState.SceneColor, new RenderGraphTextureDescriptor("pbr-glass-scene",
                RenderGraphTextureFormat.RGBA16Float, view.Width, view.Height));
            graph.UseComputePass(new("pbr-glass-snapshot"), "pbr-glass-snapshot",
                state.DeclareSnapshot, state.CopySnapshot);
        }
        graph.UsePass(new("pbr-transparent"), "pbr-transparent", state.Declare, state.Render);
    }

    private sealed class TransparencyState
    {
        internal static readonly RenderGraphTextureKey SceneColor = new("pbr-glass-scene");
        public PbrTransparentScene.View View { get; set; } = null!;
        public PbrViewState Lighting { get; set; } = null!;
        public RenderGraphTextureKey Color { get; set; }
        public RenderGraphTextureKey Depth { get; set; }
        public bool Enabled { get; set; }
        public void DeclareSnapshot(RenderGraphPassDeclarationBuilder declaration) => declaration
            .Read(Color, RenderGraphTextureUsage.CopySource).Write(SceneColor, RenderGraphTextureUsage.CopyDestination);
        public unsafe void CopySnapshot(WgpuReactiveRenderGraphPassContext context)
        {
            var source = new WGPUTexelCopyTextureInfo { Texture = (WGPUTexture*)context.GetTexture(Color).DangerousGetHandle(), Aspect = WGPUTextureAspect.All };
            var target = new WGPUTexelCopyTextureInfo { Texture = (WGPUTexture*)context.GetTexture(SceneColor).DangerousGetHandle(), Aspect = WGPUTextureAspect.All };
            var size = new WGPUExtent3D { Width = View.Width, Height = View.Height, DepthOrArrayLayers = 1 };
            WgpuUnsafe.wgpuCommandEncoderCopyTextureToTexture((WGPUCommandEncoder*)context.CommandEncoder.DangerousGetHandle(), &source, &target, &size);
        }
        public void Declare(RenderGraphPassDeclarationBuilder declaration)
        {
            if (Enabled && View.Owner.HasTransmission) {
                declaration.Read(SceneColor, RenderGraphTextureUsage.TextureBinding)
                    .Read(Depth, RenderGraphTextureUsage.TextureBinding);
            }
            View.Declare(declaration);
            declaration.ReadWrite(Color, RenderGraphTextureUsage.RenderAttachment)
                .Read(Depth, RenderGraphTextureUsage.RenderAttachment)
                .Read(_clusterConfigKey, RenderGraphBufferUsage.Uniform)
                .Read(_clusteredLightsKey, RenderGraphBufferUsage.Storage)
                .Read(_lightGridKey, RenderGraphBufferUsage.Storage)
                .Read(_lightIndexListKey, RenderGraphBufferUsage.Storage)
                .Read(_shadowAtlasKey, RenderGraphTextureUsage.TextureBinding)
                .Read(AtmosphereGpuState.IrradianceKey, RenderGraphBufferUsage.Uniform)
                .Read(_iblPrefilteredKey, RenderGraphTextureUsage.TextureBinding)
                .Read(_iblBrdfLutKey, RenderGraphTextureUsage.TextureBinding);
        }
        public void Render(WgpuReactiveRenderGraphPassContext context)
        {
            if (!Enabled) { return; }
            if (View.Owner.HasTransmission) { View.BindScene(context.GetTextureView(SceneColor), context.GetTextureView(Depth)); }
            var pass = context.GetOrBeginRenderPass(new WgpuReactiveRenderGraphColorAttachment(Color, WGPULoadOp.Load),
                new WgpuReactiveRenderGraphDepthStencilAttachment(Depth, WGPULoadOp.Undefined,
                    WGPUStoreOp.Undefined, DepthReadOnly: true));
            View.Render(pass, Lighting);
        }
    }

    internal static void UseVisibilityShadowPasses(ref RenderGraphBuildContext graph, VisibilityPbrFeature visibility,
        in RenderFeatureContext<RenderFrameContext> context) => visibility.BuildShadowGraph(ref graph, in context, _shadowAtlasKey);

    internal static void UseVisibilityLightingPass(ref RenderGraphBuildContext graph, PbrRenderer renderer,
        PbrViewState view, VisibilityPbrFeature visibility, RenderGraphTextureKey color, in RenderFrameContext frame)
    {
        var state = graph.UseState(static () => new VisibilityLightingState());
        state.Renderer = renderer; state.View = view; state.Visibility = visibility; state.Color = color; state.Frame = frame;
        graph.UsePass(new("pbr-visibility-lighting"), "pbr-visibility-lighting", state.Declare, state.Render);
    }

    private sealed class VisibilityLightingState
    {
        public PbrRenderer Renderer { get; set; } = null!;
        public PbrViewState View { get; set; } = null!;
        public VisibilityPbrFeature Visibility { get; set; } = null!;
        public RenderGraphTextureKey Color { get; set; }
        public RenderFrameContext Frame { get; set; }

        public void Declare(RenderGraphPassDeclarationBuilder declaration) => declaration
            .ReadWrite(Color, RenderGraphTextureUsage.RenderAttachment)
            .Read(Frame.DepthTarget, RenderGraphTextureUsage.TextureBinding)
            .Read(Visibility.BaseColorRoughnessTarget, RenderGraphTextureUsage.TextureBinding)
            .Read(Visibility.NormalMetallicTarget, RenderGraphTextureUsage.TextureBinding)
            .Read(Visibility.EmissiveOcclusionTarget, RenderGraphTextureUsage.TextureBinding)
            .Read(Visibility.HdrTarget, RenderGraphTextureUsage.TextureBinding)
            .Read(_clusterConfigKey, RenderGraphBufferUsage.Uniform)
            .Read(_clusteredLightsKey, RenderGraphBufferUsage.Storage)
            .Read(_lightGridKey, RenderGraphBufferUsage.Storage)
            .Read(_lightIndexListKey, RenderGraphBufferUsage.Storage)
            .Read(_shadowAtlasKey, RenderGraphTextureUsage.TextureBinding)
            .Read(AtmosphereGpuState.IrradianceKey, RenderGraphBufferUsage.Uniform)
            .Read(_iblPrefilteredKey, RenderGraphTextureUsage.TextureBinding)
            .Read(_iblBrdfLutKey, RenderGraphTextureUsage.TextureBinding);

        public void Render(WgpuReactiveRenderGraphPassContext context)
        {
            var frame = Frame.Frame;
            var pass = context.GetOrBeginRenderPass(new WgpuReactiveRenderGraphColorAttachment(Color, WGPULoadOp.Load));
            Renderer.EncodeVisibility(View, in frame, [context.GetTextureView(Frame.DepthTarget),
                context.GetTextureView(Visibility.BaseColorRoughnessTarget), context.GetTextureView(Visibility.NormalMetallicTarget),
                context.GetTextureView(Visibility.EmissiveOcclusionTarget), context.GetTextureView(Visibility.HdrTarget)], pass);
        }
    }
}
