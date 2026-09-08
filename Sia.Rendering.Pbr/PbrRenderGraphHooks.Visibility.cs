using Sia.Graphics.Reactive;
using Sia.RenderGraph;
using Sia.WebGPU;

namespace Sia.Engine.Rendering.Pbr;

public static partial class PbrRenderGraphHooks
{
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
