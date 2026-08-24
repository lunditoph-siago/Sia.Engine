using Sia.Engine.Camera;
using Sia.Engine.Lighting;
using Sia.Engine.Mesh;
using Sia.Graphics.Reactive;
using Sia.Graphics.Rendering;
using Sia.Reactive;

namespace Sia.Engine.Rendering.Pbr;

public sealed class PbrRenderFeature : IRenderFeature<RenderFrameContext>
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

    public void Configure(
        ref Hooks hooks,
        WgpuRenderGraphRegistry registry,
        in RenderFrameContext context)
    {
        var frame = context.Frame;
        var clusterConfig = frame.World.AcquireAddon<ClusterGridConfig>();
        var shadowConfig = frame.World.AcquireAddon<ShadowAtlasConfig>();

        hooks.UseClusterLightCullingPass(
            registry,
            Renderer,
            in frame,
            clusterConfig,
            shadowConfig,
            context.Camera,
            Options.ClusterCullingPass);
        hooks.UseShadowPasses(registry, Renderer, in frame, shadowConfig);
        hooks.UseIblPrecomputePasses(registry, Renderer, in frame);
        hooks.UseDepthPrepass(
            registry,
            Renderer,
            in frame,
            context.Camera,
            Options.DepthPrepass,
            context.DepthTarget);
        hooks.UseForwardPbrPass(
            registry,
            Renderer,
            in frame,
            Options.ForwardPass,
            context.ColorTarget,
            context.DepthTarget,
            context.ColorLoadOp,
            context.ColorCacheable);
    }
}
