using Sia;
using Sia.Graphics.Reactive;
using Sia.WebGPU;

namespace Sia.Engine.Rendering;

public readonly record struct RenderFrameContext(
    GpuFrame Frame,
    Entity Camera,
    RenderGraphTextureKey ColorTarget,
    RenderGraphTextureKey DepthTarget,
    WGPULoadOp ColorLoadOp = WGPULoadOp.Clear,
    bool ColorCacheable = true);
