using Sia.Graphics.Reactive;
using Sia.Graphics.Rendering;
using Sia.Reactive;

namespace Sia.Engine.Rendering.Debug;

public sealed class DebugRenderFeature : IRenderFeature<RenderFrameContext>
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

    public void Configure(
        ref Hooks hooks,
        WgpuRenderGraphRegistry registry,
        in RenderFrameContext context) =>
        hooks.UseDebugPass(registry, Renderer, in context, Options);
}
