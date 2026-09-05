using Sia.Engine.Camera;
using Sia.Engine.Lighting;
using Sia.Engine.Mesh;
using Sia.Graphics.Reactive;

namespace Sia.Engine.Rendering.Pbr;

public sealed record PbrRenderFeatureOptions
{
    public RenderGraphTextureKey HdrTarget { get; init; } = new("pbr-hdr");

    public RenderGraphPassKey SkyboxPass { get; init; } = new("pbr-skybox");

    public RenderGraphPassKey ToneMappingPass { get; init; } = new("pbr-tone-mapping");

    public RenderGraphTextureKey AtmosphereTarget { get; init; } = new("pbr-atmosphere-hdr");

    public RenderGraphPassKey AtmospherePass { get; init; } = new("pbr-atmosphere-composite");

    public float ExposureCompensation { get; init; }

    public PbrToneMapping ToneMapping { get; init; } = PbrToneMapping.Aces;

    public RenderGraphPassKey ClusterCullingPass { get; init; } = new("pbr-cluster-culling");

    public RenderGraphPassKey DepthPrepass { get; init; } = new("pbr-depth-prepass");

    public RenderGraphPassKey ForwardPass { get; init; } = new("pbr-forward");
}
