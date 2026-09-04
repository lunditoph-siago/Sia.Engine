using Sia.Graphics.Reactive;
using Sia.Reactive;
using Sia.RenderGraph;

namespace Sia.Engine.Rendering.Debug;

public static class DebugRenderGraphHooks
{
    public static void UseDebugPass(
        this ref Hooks hooks,
        WgpuRenderGraphRegistry registry,
        DebugRenderer renderer,
        in RenderFrameContext frameContext,
        DebugRenderFeatureOptions options)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(options);

        var color = frameContext.ColorTarget;
        var depth = frameContext.DepthTarget;
        var state = hooks.UseRef(() => new DebugPassState(color, depth));
        if (state.Value.Color != frameContext.ColorTarget
            || state.Value.Depth != frameContext.DepthTarget) {
            state.Value = new DebugPassState(
                frameContext.ColorTarget,
                frameContext.DepthTarget);
        }
        state.Value.Update(renderer, in frameContext, options);

        hooks.UseRenderGraphPass(
            registry,
            options.Pass,
            "debug-overlay",
            state.Value.Declare);
        hooks.UseWgpuRenderGraphPassHandler(
            registry,
            options.Pass,
            state.Value.Render);
    }

    private sealed class DebugPassState(
        RenderGraphTextureKey color,
        RenderGraphTextureKey depth)
    {
        private DebugRenderer? _renderer;
        private RenderFrameContext _frameContext;
        private DebugRenderFeatureOptions? _options;

        public RenderGraphTextureKey Color { get; } = color;

        public RenderGraphTextureKey Depth { get; } = depth;

        public void Update(
            DebugRenderer renderer,
            in RenderFrameContext frameContext,
            DebugRenderFeatureOptions options)
        {
            _renderer = renderer;
            _frameContext = frameContext;
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
            var frame = _frameContext.Frame;
            _renderer!.Encode(in frame, _frameContext.Camera, renderPass);
        }
    }
}
