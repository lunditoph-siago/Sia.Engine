using Sia.Graphics.Reactive;
using Sia.RenderGraph;

namespace Sia.Engine.Rendering.Debug;

public static class DebugRenderGraphHooks
{
    public static void UseDebugPass(
        ref RenderGraphBuildContext graph,
        DebugRenderer renderer,
        in RenderFrameContext frameContext,
        DebugRenderFeatureOptions options)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(options);

        var color = frameContext.ColorTarget;
        var depth = frameContext.DepthTarget;
        var state = graph.UseState(() => new DebugPassState(color, depth));
        state.Update(renderer, color, depth, options);

        graph.UsePass(
            options.Pass,
            "debug-overlay",
            state.Declare,
            state.Render);
    }

    private sealed class DebugPassState(
        RenderGraphTextureKey color,
        RenderGraphTextureKey depth)
    {
        private DebugRenderer? _renderer;
        private DebugRenderFeatureOptions? _options;

        public RenderGraphTextureKey Color { get; private set; } = color;

        public RenderGraphTextureKey Depth { get; private set; } = depth;

        public void Update(
            DebugRenderer renderer,
            RenderGraphTextureKey color,
            RenderGraphTextureKey depth,
            DebugRenderFeatureOptions options)
        {
            _renderer = renderer;
            Color = color;
            Depth = depth;
            _options = options;
        }

        public void Declare(RenderGraphPassDeclarationBuilder declaration) =>
            declaration
                .Write(Color, RenderGraphTextureUsage.RenderAttachment)
                .Write(Depth, RenderGraphTextureUsage.RenderAttachment);

        public void Render(WgpuReactiveRenderGraphPassContext context)
        {
            var options = _options!;
            var renderPass = context.GetOrBeginRenderPass(
                new WgpuReactiveRenderGraphColorAttachment(
                    Color,
                    options.ColorLoadOp,
                    Cacheable: options.ColorCacheable),
                new WgpuReactiveRenderGraphDepthStencilAttachment(
                    Depth,
                    options.DepthLoadOp));
            _renderer!.Encode(renderPass);
        }
    }
}
