using Sia.Math;

namespace Sia.Engine.Rendering.Pbr;

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

    public void Validate()
    {
        Positive(GroundRadiusKilometers, nameof(GroundRadiusKilometers));
        Positive(AtmosphereHeightKilometers, nameof(AtmosphereHeightKilometers));
        Positive(KilometersPerWorldUnit, nameof(KilometersPerWorldUnit));
        Positive(RayleighScaleHeightKilometers, nameof(RayleighScaleHeightKilometers));
        Positive(MieScaleHeightKilometers, nameof(MieScaleHeightKilometers));
        Positive(OzoneHalfWidthKilometers, nameof(OzoneHalfWidthKilometers));
        Positive(AerialPerspectiveDistanceKilometers, nameof(AerialPerspectiveDistanceKilometers));
        if (!float.IsFinite(WorldOriginAltitudeKilometers) || !float.IsFinite(OzoneCenterKilometers)
            || !float.IsFinite(GroundRadiusKilometers + AtmosphereHeightKilometers)
            || GroundRadiusKilometers + AtmosphereHeightKilometers <= GroundRadiusKilometers) {
            throw new ArgumentOutOfRangeException(nameof(AtmosphereHeightKilometers));
        }
        if (!float.IsFinite(MieAnisotropy) || MathF.Abs(MieAnisotropy) >= 0.99f) {
            throw new ArgumentOutOfRangeException(nameof(MieAnisotropy));
        }
        if (!float.IsFinite(SunAngularRadiusRadians) || SunAngularRadiusRadians is < 0.0001f or > 0.1f) {
            throw new ArgumentOutOfRangeException(nameof(SunAngularRadiusRadians));
        }
        Positive(math.dot(SunDirection, SunDirection), nameof(SunDirection));
        Color(RayleighScatteringPerKilometer, nameof(RayleighScatteringPerKilometer));
        Color(MieScatteringPerKilometer, nameof(MieScatteringPerKilometer));
        Color(MieAbsorptionPerKilometer, nameof(MieAbsorptionPerKilometer));
        Color(OzoneAbsorptionPerKilometer, nameof(OzoneAbsorptionPerKilometer));
        Color(SolarIrradiance, nameof(SolarIrradiance));
        Color(GroundAlbedo, nameof(GroundAlbedo));
        if (GroundAlbedo.x > 1 || GroundAlbedo.y > 1 || GroundAlbedo.z > 1) {
            throw new ArgumentOutOfRangeException(nameof(GroundAlbedo));
        }
    }

    private static void Positive(float value, string name)
    {
        if (!float.IsFinite(value) || value <= 0) {
            throw new ArgumentOutOfRangeException(name);
        }
    }

    private static void Color(float3 value, string name)
    {
        if (!float.IsFinite(value.x) || !float.IsFinite(value.y) || !float.IsFinite(value.z)
            || value.x < 0 || value.y < 0 || value.z < 0) {
            throw new ArgumentOutOfRangeException(name);
        }
    }
}
