namespace Sia.Engine.Rendering.Debug;

public sealed class DebugRenderFeature :
    IPrepareRenderFeature<RenderFrameContext>,
    IRenderGraphContributor<RenderFrameContext>
{
    public static RenderFeatureKey FeatureKey { get; } = new("debug");

    public RenderFeatureKey Key => FeatureKey;

    public DebugRenderer Renderer { get; }

    public DebugRenderFeatureOptions Options { get; }

    public DebugRenderFeature(
        DebugRenderer renderer,
        DebugRenderFeatureOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        Renderer = renderer;
        Options = options ?? new DebugRenderFeatureOptions();
    }

    public void Prepare(in RenderFeatureContext<RenderFrameContext> context)
    {
        var frame = context.Frame.Frame;
        Renderer.Prepare(in frame, context.Frame.Camera);
    }

    public void BuildRenderGraph(
        ref RenderGraphBuildContext graph,
        in RenderFeatureContext<RenderFrameContext> context)
    {
        var frame = context.Frame;
        DebugRenderGraphHooks.UseDebugPass(ref graph, Renderer, in frame, Options);
    }
}
