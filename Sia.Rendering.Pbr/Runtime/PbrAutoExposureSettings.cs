namespace Sia.Engine.Rendering.Pbr;

/// <summary>Optional instantaneous, uniform-pixel geometric-mean HDR metering. No temporal adaptation.</summary>
public sealed record PbrAutoExposureSettings
{
    public float TargetLuminance { get; init; } = .18f;
    /// <summary>Lower bound on total display stops, including exposure compensation.</summary>
    public float MinimumStops { get; init; } = -16;
    /// <summary>Upper bound on total display stops, including exposure compensation.</summary>
    public float MaximumStops { get; init; } = 16;

    internal void Validate()
    {
        if (!float.IsFinite(TargetLuminance) || TargetLuminance is <= 0 or > 1
            || !float.IsFinite(MinimumStops) || MinimumStops is < -16 or > 16
            || !float.IsFinite(MaximumStops) || MaximumStops is < -16 or > 16
            || MinimumStops > MaximumStops)
            throw new ArgumentOutOfRangeException(nameof(PbrAutoExposureSettings));
    }
}
