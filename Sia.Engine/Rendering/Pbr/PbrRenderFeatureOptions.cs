using Sia.Engine.Camera;
using Sia.Engine.Lighting;
using Sia.Engine.Mesh;
using Sia.Graphics.Reactive;

namespace Sia.Engine.Rendering.Pbr;

public sealed record PbrRenderFeatureOptions
{
    public RenderGraphPassKey ClusterCullingPass { get; init; } = new("pbr-cluster-culling");

    public RenderGraphPassKey DepthPrepass { get; init; } = new("pbr-depth-prepass");

    public RenderGraphPassKey ForwardPass { get; init; } = new("pbr-forward");
}
