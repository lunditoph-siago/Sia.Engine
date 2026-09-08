using Sia.Engine.Camera;
using Sia.Engine.Lighting;
using Sia.Engine.Mesh;
using Sia.RenderGraph;
using Sia.WebGPU;

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
    public VisibilityPbrFeature? Visibility { get; }

    public PbrRenderFeature(
        PbrRenderer renderer,
        PbrRenderFeatureOptions? options = null,
        VisibilityPbrFeature? visibility = null)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        Renderer = renderer;
        Options = options ?? new PbrRenderFeatureOptions();
        Visibility = visibility;
    }

    public void Extract(in RenderFeatureContext<RenderFrameContext> context)
    {
        Visibility?.Extract(in context);
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
        Renderer.PrepareOutput(state, in frame, extracted, Options.ExposureCompensation, Options.ToneMapping);
        if (Visibility is { } visibility) {
            visibility.Prepare(in context, sceneLighting: true);
            visibility.PrepareShadows(in context, state.Shadows, extracted.ShadowConfig);
            Renderer.PrepareVisibility(state, in frame, extracted, visibility.DebugMode);
        }
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

        if (Options.HdrTarget == frameContext.ColorTarget) {
            throw new InvalidOperationException("The HDR intermediate and output target must be distinct.");
        }
        graph.UseTexture(Options.HdrTarget, new RenderGraphTextureDescriptor(
            "pbr-hdr", RenderGraphTextureFormat.RGBA16Float,
            (uint)extracted.Viewport.Width, (uint)extracted.Viewport.Height));

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
        PbrRenderGraphHooks.UseSkyboxPass(
            ref graph, Renderer, state, Options.SkyboxPass, Options.HdrTarget);
        if (Visibility is { } visibility) {
            if (Options.HdrTarget == visibility.HdrTarget || Options.HdrTarget == visibility.VisibilityTarget
                || Options.HdrTarget == visibility.BaseColorRoughnessTarget || Options.HdrTarget == visibility.NormalMetallicTarget
                || Options.HdrTarget == visibility.EmissiveOcclusionTarget || frameContext.ColorTarget == visibility.HdrTarget) {
                throw new InvalidOperationException("Visibility surfaces and scene output require distinct HDR targets.");
            }
            visibility.BuildRenderGraph(ref graph, in context, includeOutput: false);
            PbrRenderGraphHooks.UseVisibilityShadowPasses(ref graph, visibility, in context);
            PbrRenderGraphHooks.UseVisibilityLightingPass(ref graph, Renderer, state, visibility, Options.HdrTarget, in frameContext);
        } else {
            PbrRenderGraphHooks.UseDepthPrepass(
                ref graph, Renderer, state, phase, Options.DepthPrepass, frameContext.DepthTarget);
            PbrRenderGraphHooks.UseForwardPbrPass(
                ref graph, Renderer, state, phase, Options.ForwardPass, Options.HdrTarget, frameContext.DepthTarget, WGPULoadOp.Load);
        }
        var output = PbrRenderGraphHooks.UseAtmosphereComposite(
            ref graph, state, extracted, Options, in frameContext);
        PbrRenderGraphHooks.UseToneMappingPass(
            ref graph, Renderer, state, Options.ToneMappingPass, output, in frameContext);
    }
}
