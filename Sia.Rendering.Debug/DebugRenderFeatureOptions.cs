using Sia.Graphics.Reactive;
using Sia.WebGPU;

namespace Sia.Engine.Rendering.Debug;

public sealed record DebugRenderFeatureOptions
{
    public RenderGraphPassKey Pass { get; init; } = new("debug-overlay");

    public WGPULoadOp ColorLoadOp { get; init; } = WGPULoadOp.Load;

    public WGPULoadOp DepthLoadOp { get; init; } = WGPULoadOp.Load;

    public bool ColorCacheable { get; init; }
}
