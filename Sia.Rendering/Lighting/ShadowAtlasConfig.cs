using Sia.Engine.Camera;
using Sia.Engine.Lighting;
using Sia.Engine.Mesh;
using Sia;

namespace Sia.Engine.Rendering;

public sealed class ShadowAtlasConfig : IAddon
{
    public uint TileResolution { get; set; } = 1024;
    public int CascadeCount { get; set; } = 3;
    public float CascadeSplitLambda { get; set; } = 0.5f;
    public float CascadeShadowPullback { get; set; } = 2.0f;
    public float ShadowDistance { get; set; } = 40.0f;
    public int MaxShadowedSpotLights { get; set; } = 4;
    /// <summary>Point cube capacity, 0..4, fixed when a PBR view is created. Each light owns six cached layers.</summary>
    public int MaxShadowedPointLights { get; set; }
    /// <summary>Directional receiver offset toward the light, in world units; independent of cascade depth range.</summary>
    public float DirectionalShadowConstantBias { get; set; } = .001f;
    /// <summary>Additional directional offset in world units, multiplied by the bounded sine of the geometric incidence angle.</summary>
    public float DirectionalShadowSlopeBias { get; set; } = .002f;
    /// <summary>Point-shadow receiver offset toward the light, in world units; independent of projection range.</summary>
    public float PointShadowConstantBias { get; set; } = .001f;
    /// <summary>Additional point-shadow offset in world units, multiplied by the bounded sine of the geometric incidence angle.</summary>
    public float PointShadowSlopeBias { get; set; } = .002f;

    public int LayerCount => checked(CascadeCount + MaxShadowedSpotLights + MaxShadowedPointLights * 6);
}
