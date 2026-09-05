using Sia.Engine.Camera;
using Sia.Engine.Lighting;
using Sia.Engine.Mesh;

namespace Sia.Engine.Rendering.Pbr;

public sealed class PbrRenderFeature :
    IExtractRenderFeature<RenderFrameContext>,
    IPrepareRenderFeature<RenderFrameContext>,
    IQueueRenderFeature<RenderFrameContext>,
    IRenderGraphContributor<RenderFrameContext>
{
    public static RenderFeatureKey FeatureKey { get; } = new("pbr");

    public RenderFeatureKey Key => FeatureKey;

    public PbrRenderer Renderer { get; }

    public PbrRenderFeatureOptions Options { get; }

    public PbrRenderFeature(
        PbrRenderer renderer,
        PbrRenderFeatureOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        Renderer = renderer;
        Options = options ?? new PbrRenderFeatureOptions();
    }

    public void Extract(in RenderFeatureContext<RenderFrameContext> context)
    {
        var frameContext = context.Frame;
        var frame = frameContext.Frame;
        var clusterConfig = frame.MainWorld.AcquireAddon<ClusterGridConfig>();
        var shadowConfig = frame.MainWorld.AcquireAddon<ShadowAtlasConfig>();
        var state = context.View.PersistentResources.GetOrAdd(static () => new PbrViewState());
        var extracted = Renderer.ExtractFrame(
            state,
            in frame,
            frameContext.Camera,
            clusterConfig,
            shadowConfig);
        context.View.Resources.Set(extracted);
    }

    public void Prepare(in RenderFeatureContext<RenderFrameContext> context)
    {
        var frame = context.Frame.Frame;
        var state = context.View.PersistentResources.GetRequired<PbrViewState>();
        var extracted = context.View.Resources.GetRequired<PbrExtractedView>();
        Renderer.PrepareFrame(state, in frame, extracted);
        Renderer.PrepareLighting(state, in frame, extracted);
    }

    public void Queue(in RenderFeatureContext<RenderFrameContext> context)
    {
        var phase = context.View.Phases.GetOrAdd(
            PbrRenderPhases.Opaque,
            PbrDrawItemComparer.Instance);
        var extracted = context.View.Resources.GetRequired<PbrExtractedView>();
        Renderer.QueueOpaque(extracted, phase);
    }

    public void BuildRenderGraph(
        ref RenderGraphBuildContext graph,
        in RenderFeatureContext<RenderFrameContext> context)
    {
        var frameContext = context.Frame;
        var state = context.View.PersistentResources.GetRequired<PbrViewState>();
        var extracted = context.View.Resources.GetRequired<PbrExtractedView>();
        var clusterConfig = extracted.ClusterConfig;
        var shadowConfig = extracted.ShadowConfig;
        var phase = context.View.Phases.GetRequired<PbrDrawItem>(PbrRenderPhases.Opaque);

        PbrRenderGraphHooks.UseClusterLightCullingPass(
            ref graph,
            Renderer,
            state,
            clusterConfig,
            Options.ClusterCullingPass);
        PbrRenderGraphHooks.UseShadowPasses(
            ref graph, Renderer, state, shadowConfig, extracted.AllItems);
        PbrRenderGraphHooks.UseIblPrecomputePasses(
            ref graph, Renderer, state);
        PbrRenderGraphHooks.UseDepthPrepass(
            ref graph,
            Renderer,
            state,
            phase,
            Options.DepthPrepass,
            frameContext.DepthTarget);
        PbrRenderGraphHooks.UseForwardPbrPass(
            ref graph,
            Renderer,
            state,
            phase,
            Options.ForwardPass,
            frameContext.ColorTarget,
            frameContext.DepthTarget,
            frameContext.ColorLoadOp,
            frameContext.ColorCacheable);
    }
}
