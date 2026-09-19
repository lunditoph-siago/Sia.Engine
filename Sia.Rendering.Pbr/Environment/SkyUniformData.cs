using System.Runtime.InteropServices;
using Sia.Math;

namespace Sia.Engine.Rendering.Pbr;

[StructLayout(LayoutKind.Sequential)]
public readonly record struct SkyUniformData(
    float4 Horizon, float4 Zenith, float4 Ground, float4 SunDirection, float4 SunRadiance)
{
    public const int Stride = 80;

    public static SkyUniformData From(ProceduralSky sky) => new(
        new float4(sky.Horizon, sky.Intensity), new float4(sky.Zenith, sky.SunExponent),
        new float4(sky.Ground, 0), new float4(math.normalize(sky.SunDirection), 0),
        new float4(sky.SunRadiance, 0));
}
