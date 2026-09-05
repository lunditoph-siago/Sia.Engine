using Sia.Engine.Camera;
using Sia.Math;

namespace Sia.Engine.Rendering.Pbr;

public sealed record PbrExtractedView(
    CameraMatrices CameraMatrices,
    global::Sia.Engine.Camera.Camera Camera,
    ViewportSize Viewport,
    ClusterGridConfig ClusterConfig,
    ShadowAtlasConfig ShadowConfig,
    PbrRenderInstance[] Instances,
    PbrDrawItem[] AllItems,
    PbrDrawItem[] VisibleItems,
    ProceduralSky Sky,
    float4[]? IrradianceCoefficients,
    SkyAtmosphere? Atmosphere = null);
