using System.Diagnostics;
using System.Runtime.CompilerServices;
using Sia.Math;

namespace Sia.Engine.Rendering;

public sealed partial record SkyAtmosphere
{
    public float GroundRadiusKilometers { get; init; } = 6360;
    public float AtmosphereHeightKilometers { get; init; } = 100;
    public float KilometersPerWorldUnit { get; init; } = 0.001f;
    public float WorldOriginAltitudeKilometers { get; init; }
    public float3 RayleighScatteringPerKilometer { get; init; } = new(0.005802f, 0.013558f, 0.0331f);
    public float RayleighScaleHeightKilometers { get; init; } = 8;
    public float3 MieScatteringPerKilometer { get; init; } = new(0.003996f);
    public float3 MieAbsorptionPerKilometer { get; init; } = new(0.000444f);
    public float MieScaleHeightKilometers { get; init; } = 1.2f;
    public float MieAnisotropy { get; init; } = 0.8f;
    public float3 OzoneAbsorptionPerKilometer { get; init; } = new(0.000650f, 0.001881f, 0.000085f);
    public float OzoneCenterKilometers { get; init; } = 25;
    public float OzoneHalfWidthKilometers { get; init; } = 15;
    public float3 GroundAlbedo { get; init; } = new(0.3f);
    public float3 SunDirection { get; init; } = math.normalize(new float3(0.4f, 1, 0.3f));
    public float3 SolarIrradiance { get; init; } = new(20);
    public float SunAngularRadiusRadians { get; init; } = 0.004675f;
    public float AerialPerspectiveDistanceKilometers { get; init; } = 32;

    [Conditional("DEBUG")]
    public void Validate()
    {
        Require(IsPositive(GroundRadiusKilometers), nameof(GroundRadiusKilometers));
        Require(IsPositive(AtmosphereHeightKilometers), nameof(AtmosphereHeightKilometers));
        Require(IsPositive(KilometersPerWorldUnit), nameof(KilometersPerWorldUnit));

        Require(float.IsFinite(WorldOriginAltitudeKilometers), nameof(WorldOriginAltitudeKilometers));

        Require(IsPositive(RayleighScaleHeightKilometers), nameof(RayleighScaleHeightKilometers));
        Require(IsPositive(MieScaleHeightKilometers), nameof(MieScaleHeightKilometers));
        Require(float.IsFinite(MieAnisotropy) && MathF.Abs(MieAnisotropy) < 0.99f, nameof(MieAnisotropy));

        Require(float.IsFinite(OzoneCenterKilometers), nameof(OzoneCenterKilometers));
        Require(IsPositive(OzoneHalfWidthKilometers), nameof(OzoneHalfWidthKilometers));

        Require(float.IsFinite(SunAngularRadiusRadians) && SunAngularRadiusRadians is >= 0.0001f and <= 0.1f, nameof(SunAngularRadiusRadians));

        Require(IsPositive(math.dot(SunDirection, SunDirection)), nameof(SunDirection));
        Require(IsPositive(AerialPerspectiveDistanceKilometers), nameof(AerialPerspectiveDistanceKilometers));

        var atmosphereRadius = GroundRadiusKilometers + AtmosphereHeightKilometers;
        Require(float.IsFinite(atmosphereRadius) && atmosphereRadius > GroundRadiusKilometers, nameof(AtmosphereHeightKilometers));

        Require(IsColor(RayleighScatteringPerKilometer), nameof(RayleighScatteringPerKilometer));
        Require(IsColor(MieScatteringPerKilometer), nameof(MieScatteringPerKilometer));
        Require(IsColor(MieAbsorptionPerKilometer), nameof(MieAbsorptionPerKilometer));
        Require(IsColor(OzoneAbsorptionPerKilometer), nameof(OzoneAbsorptionPerKilometer));
        Require(IsColor(SolarIrradiance), nameof(SolarIrradiance));

        Require(IsColor(GroundAlbedo) && GroundAlbedo.x <= 1 && GroundAlbedo.y <= 1 && GroundAlbedo.z <= 1, nameof(GroundAlbedo));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Require(bool condition, string name)
    {
        if (!condition) throw new ArgumentOutOfRangeException(name);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsPositive(float value)
        => value > 0 && float.IsFinite(value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsColor(float3 value)
        => math.all(math.isfinite(value) & (value >= 0));
}
