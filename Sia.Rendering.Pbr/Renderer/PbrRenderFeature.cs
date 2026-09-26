using Sia.Engine.Lighting;
using Sia.RenderGraph;
using Sia.WebGPU;

namespace Sia.Engine.Rendering.Pbr;

public sealed class PbrRenderFeature :
    IExtractRenderFeature<RenderFrameContext>,
    IPrepareRenderFeature<RenderFrameContext>,
    IRenderGraphContributor<RenderFrameContext>
{
    public static RenderFeatureKey FeatureKey { get; } = new("pbr");

    public RenderFeatureKey Key => FeatureKey;

    public PbrRenderer Renderer { get; }

    public PbrRenderFeatureOptions Options { get; }
    public VisibilityPbrFeature Visibility { get; }
    public PbrTransparentScene? Transparency { get; }

    public PbrRenderFeature(
        PbrRenderer renderer,
        VisibilityPbrFeature visibility,
        PbrRenderFeatureOptions? options = null,
        PbrTransparentScene? transparency = null)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(visibility);
        Renderer = renderer;
        Options = options ?? new PbrRenderFeatureOptions();
        Visibility = visibility;
        Transparency = transparency;
    }

    public void Extract(in RenderFeatureContext<RenderFrameContext> context)
    {
        Visibility.Extract(in context);
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
            shadowConfig,
            Visibility.ShadowBounds);
        context.View.Resources.Set(extracted);
    }

    public void Prepare(in RenderFeatureContext<RenderFrameContext> context)
    {
        var frame = context.Frame.Frame;
        var state = context.View.PersistentResources.GetRequired<PbrViewState>();
        var extracted = context.View.Resources.GetRequired<PbrExtractedView>();
        Renderer.PrepareLighting(state, in frame, extracted);
        Renderer.PrepareOutput(state, in frame, extracted, Options.ExposureCompensation, Options.ToneMapping);
        Transparency?.Prepare(in context, extracted);
        if (!Options.ScreenSpaceReflections && !Options.ScreenSpaceIndirectLighting) Visibility.PrepareFusedLighting();
        Visibility.Prepare(in context, sceneLighting: true);
        Visibility.PrepareShadows(in context, state, extracted.ShadowConfig);
        if (!Visibility.FusedLighting) Renderer.PrepareVisibility(state, in frame, extracted, Visibility.DebugMode);
        if (Options.ScreenSpaceReflections || Options.ScreenSpaceIndirectLighting) {
            state.ScreenLighting ??= new PbrScreenLighting(in frame, Options.ScreenSpaceReflections, Options.ScreenSpaceIndirectLighting);
            state.ScreenLighting.Prepare(in frame, extracted);
        }
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

        if (Options.HdrTarget == frameContext.ColorTarget) {
            throw new InvalidOperationException("The HDR intermediate and output target must be distinct.");
        }
        graph.UseTexture(Options.HdrTarget, new RenderGraphTextureDescriptor(
            "pbr-hdr", RenderGraphTextureFormat.RGBA16Float,
            (uint)extracted.Viewport.Width, (uint)extracted.Viewport.Height));

        Visibility.BuildFrameTiming(ref graph, in context, begin: true);
        PbrRenderGraphHooks.UseClusterLightCullingPass(
            ref graph,
            Renderer,
            state,
            clusterConfig,
            Options.ClusterCullingPass);
        PbrRenderGraphHooks.UseShadowAtlas(ref graph, state, shadowConfig);
        PbrRenderGraphHooks.UseIblPrecomputePasses(
            ref graph, Renderer, state);
        PbrRenderGraphHooks.UseSkyboxPass(
            ref graph, Renderer, state, Options.SkyboxPass, Options.HdrTarget);
        var visibility = Visibility;
        if (Options.HdrTarget == visibility.HdrTarget || Options.HdrTarget == visibility.VisibilityTarget
            || Options.HdrTarget == visibility.BaseColorRoughnessTarget || Options.HdrTarget == visibility.NormalMetallicTarget
            || Options.HdrTarget == visibility.EmissiveOcclusionTarget || frameContext.ColorTarget == visibility.HdrTarget) {
            throw new InvalidOperationException("Visibility surfaces and scene output require distinct HDR targets.");
        }
        visibility.BuildRenderGraph(ref graph, in context, includeOutput: false);
        PbrRenderGraphHooks.UseVisibilityShadowPasses(ref graph, visibility, in context);
        if (visibility.FusedLighting) visibility.BuildFusedLighting(ref graph, in context, state, Options.HdrTarget);
        else PbrRenderGraphHooks.UseVisibilityLightingPass(ref graph, Renderer, state, visibility, Options.HdrTarget, in frameContext);
        var hdr = Options.HdrTarget;
        if (Options.ScreenSpaceReflections || Options.ScreenSpaceIndirectLighting) {
            hdr = state.ScreenLighting!.BuildGraph(ref graph, hdr, frameContext.DepthTarget,
                visibility, state, extracted);
        }
        if (hdr == frameContext.ColorTarget) {
            throw new InvalidOperationException("Screen lighting and final output require distinct targets.");
        }
        if (Transparency is { } transparency) {
            PbrRenderGraphHooks.UseTransparencyPass(ref graph, transparency.Import(ref graph, in context), state,
                hdr, frameContext.DepthTarget, Visibility.DebugMode == VisibilityDebugMode.Shaded);
        }
        var output = PbrRenderGraphHooks.UseAtmosphereComposite(
            ref graph, state, extracted, Options, hdr, in frameContext);
        PbrRenderGraphHooks.UseToneMappingPass(
            ref graph, Renderer, state, Options.ToneMappingPass, output, in frameContext);
        Visibility.BuildFrameTiming(ref graph, in context, begin: false);
    }
}
