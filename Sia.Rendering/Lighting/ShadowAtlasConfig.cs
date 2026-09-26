using Sia.Engine.Camera;
using Sia.Engine.Lighting;
using Sia.Engine.Mesh;
using Sia;

namespace Sia.Engine.Rendering;

public sealed class ShadowAtlasConfig : IAddon
{
    /// <summary>Fit a single directional map to scene bounds, independent of the camera.</summary>
    public bool SceneBoundsDirectional { get; set; }
    /// <summary>Use twelve conservative triangles per instance for depth-only shadows.</summary>
    public bool ConservativeCasterBounds { get; set; }
    public uint TileResolution { get; set; } = 1024;
    public int CascadeCount { get; set; } = 3;
    public float CascadeSplitLambda { get; set; } = 0.5f;
    public float CascadeShadowPullback { get; set; } = 2.0f;
    public float ShadowDistance { get; set; } = 40.0f;
    public int MaxShadowedSpotLights { get; set; } = 4;

    public int LayerCount => CascadeCount + MaxShadowedSpotLights;
}
