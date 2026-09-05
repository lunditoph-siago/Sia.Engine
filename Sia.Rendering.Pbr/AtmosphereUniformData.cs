using System.Runtime.InteropServices;
using Sia.Engine.Camera;
using Sia.Math;

namespace Sia.Engine.Rendering.Pbr;

[StructLayout(LayoutKind.Sequential)]
internal readonly record struct AtmosphereUniformData(
    float4 Radii, float4 Rayleigh, float4 Mie, float4 MieAbsorption,
    float4 Ozone, float4 Ground, float4 Sun, float4 Irradiance,
    float4 CameraPlanet, float4 CameraWorld, float4x4 InverseViewProjection)
{
    public const uint Stride = 224;

    public static AtmosphereUniformData From(SkyAtmosphere sky, in CameraMatrices camera)
    {
        var position = camera.WorldPosition * sky.KilometersPerWorldUnit
            + new float3(0, sky.GroundRadiusKilometers + sky.WorldOriginAltitudeKilometers, 0);
        var radius = math.length(position);
        if (!float.IsFinite(radius) || radius < sky.GroundRadiusKilometers) {
            throw new ArgumentOutOfRangeException(nameof(camera), "The atmosphere camera must be above the virtual planet ground.");
        }
        if (radius - sky.GroundRadiusKilometers < 0.001f) {
            position = math.normalize(position) * (sky.GroundRadiusKilometers + 0.001f);
        }
        return new(
            new(sky.GroundRadiusKilometers, sky.GroundRadiusKilometers + sky.AtmosphereHeightKilometers,
                sky.KilometersPerWorldUnit, sky.AerialPerspectiveDistanceKilometers),
            new(sky.RayleighScatteringPerKilometer, sky.RayleighScaleHeightKilometers),
            new(sky.MieScatteringPerKilometer, sky.MieScaleHeightKilometers),
            new(sky.MieAbsorptionPerKilometer, sky.MieAnisotropy),
            new(sky.OzoneAbsorptionPerKilometer, sky.OzoneCenterKilometers),
            new(sky.GroundAlbedo, sky.OzoneHalfWidthKilometers),
            new(math.normalize(sky.SunDirection), 4 * MathF.Pow(MathF.Sin(sky.SunAngularRadiusRadians * 0.5f), 2)),
            new(sky.SolarIrradiance, MathF.PI * MathF.Pow(MathF.Sin(sky.SunAngularRadiusRadians), 2)),
            new(position, 0), new(camera.WorldPosition, 0), camera.InvViewProj);
    }
}
