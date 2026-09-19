using Sia.Graphics.Reactive;
using Sia.RenderGraph;
using Sia.WebGPU;

namespace Sia.Engine.Rendering.Pbr;

public static partial class PbrRenderGraphHooks
{
    internal static RenderGraphTextureKey UseAtmosphereComposite(ref RenderGraphBuildContext graph,
        PbrViewState view, PbrExtractedView extracted, PbrRenderFeatureOptions options, in RenderFrameContext frame)
    {
        var state = graph.UseState(static () => new AtmosphereCompositeState());
        if (options.AtmosphereTarget == options.HdrTarget || options.AtmosphereTarget == frame.ColorTarget
            || options.AtmosphereTarget == frame.DepthTarget) {
            throw new InvalidOperationException("Atmosphere composition requires a distinct HDR target.");
        }
        graph.UseTexture(options.AtmosphereTarget, new RenderGraphTextureDescriptor("atmosphere-hdr", RenderGraphTextureFormat.RGBA16Float,
            (uint)extracted.Viewport.Width, (uint)extracted.Viewport.Height));
        state.Atmosphere = view.ActiveAtmosphere is null ? null : view.Atmosphere;
        state.Frame = frame;
        state.Source = options.HdrTarget;
        state.Target = options.AtmosphereTarget;
        graph.UsePass(options.AtmospherePass, "atmosphere-composite", state.Declare, state.Render);
        return state.Atmosphere is null ? options.HdrTarget : options.AtmosphereTarget;
    }

    private sealed class AtmosphereCompositeState
    {
        public AtmosphereGpuState? Atmosphere { get; set; }
        public RenderFrameContext Frame { get; set; }
        public RenderGraphTextureKey Source { get; set; }
        public RenderGraphTextureKey Target { get; set; }

        public void Declare(RenderGraphPassDeclarationBuilder declaration)
        {
            if (Atmosphere is null) { return; }
            AtmosphereGpuState.DeclareSkyRead(declaration);
            declaration.Read(Source, RenderGraphTextureUsage.TextureBinding)
                .Read(Frame.DepthTarget, RenderGraphTextureUsage.TextureBinding)
                .Read(AtmosphereGpuState.TextureKeys[3], RenderGraphTextureUsage.TextureBinding)
                .Read(AtmosphereGpuState.TextureKeys[4], RenderGraphTextureUsage.TextureBinding)
                .Write(Target, RenderGraphTextureUsage.RenderAttachment);
        }

        public void Render(WgpuReactiveRenderGraphPassContext context)
        {
            if (Atmosphere is null) { return; }
            var frame = Frame.Frame;
            var source = context.GetTextureView(Source);
            var depth = context.GetTextureView(Frame.DepthTarget);
            var pass = context.GetOrBeginRenderPass(new WgpuReactiveRenderGraphColorAttachment(Target, WGPULoadOp.Clear));
            Atmosphere.EncodeComposite(frame, source, depth, pass);
        }
    }
}
