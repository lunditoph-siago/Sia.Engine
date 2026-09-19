using Sia.Graphics.Reactive;
using Sia.RenderGraph;
using Sia.WebGPU;

namespace Sia.Engine.Rendering.Pbr;

public static partial class PbrRenderGraphHooks
{
    public static void UseSkyboxPass(ref RenderGraphBuildContext graph, PbrRenderer renderer,
        PbrViewState view, RenderGraphPassKey pass, RenderGraphTextureKey color)
    {
        var state = graph.UseState(static () => new SkyboxState());
        state.Renderer = renderer;
        state.View = view;
        state.Color = color;
        graph.UsePass(pass, "pbr-skybox", state.Declare, state.Render);
    }

    public static void UseToneMappingPass(ref RenderGraphBuildContext graph, PbrRenderer renderer,
        PbrViewState view, RenderGraphPassKey pass, RenderGraphTextureKey hdr, in RenderFrameContext frame)
    {
        var state = graph.UseState(static () => new ToneMappingState());
        state.Renderer = renderer;
        state.View = view;
        state.Hdr = hdr;
        state.Frame = frame;
        graph.UsePass(pass, "pbr-tone-mapping", state.Declare, state.Render);
    }

    private sealed class SkyboxState
    {
        public PbrRenderer Renderer { get; set; } = null!;
        public PbrViewState View { get; set; } = null!;
        public RenderGraphTextureKey Color { get; set; }

        public void Declare(RenderGraphPassDeclarationBuilder declaration)
        {
            declaration.Read(_iblPrefilteredKey, RenderGraphTextureUsage.TextureBinding)
                .Write(Color, RenderGraphTextureUsage.RenderAttachment);
            if (View.ActiveAtmosphere is not null) {
                AtmosphereGpuState.DeclareSkyRead(declaration);
            }
        }

        public void Render(WgpuReactiveRenderGraphPassContext context)
        {
            var pass = context.GetOrBeginRenderPass(new WgpuReactiveRenderGraphColorAttachment(Color, WGPULoadOp.Clear));
            Renderer.EncodeSkybox(View, pass);
        }
    }

    private sealed class ToneMappingState
    {
        public PbrRenderer Renderer { get; set; } = null!;
        public PbrViewState View { get; set; } = null!;
        public RenderGraphTextureKey Hdr { get; set; }
        public RenderFrameContext Frame { get; set; }

        public void Declare(RenderGraphPassDeclarationBuilder declaration) => declaration
            .Read(Hdr, RenderGraphTextureUsage.TextureBinding)
            .Write(Frame.ColorTarget, RenderGraphTextureUsage.RenderAttachment);

        public void Render(WgpuReactiveRenderGraphPassContext context)
        {
            var source = context.GetTextureView(Hdr);
            var pass = context.GetOrBeginRenderPass(new WgpuReactiveRenderGraphColorAttachment(
                Frame.ColorTarget, Frame.ColorLoadOp, Cacheable: Frame.ColorCacheable));
            var frame = Frame.Frame;
            Renderer.EncodeToneMapping(View, in frame, source, pass);
        }
    }
}
