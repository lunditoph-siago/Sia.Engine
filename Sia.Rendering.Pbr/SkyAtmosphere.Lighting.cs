using Sia.Math;

namespace Sia.Engine.Rendering.Pbr;

public sealed partial record SkyAtmosphere
{
    public float3 EvaluateSunIrradiance(float3 worldPosition)
    {
        Validate();
        var x = (double)worldPosition.x * KilometersPerWorldUnit;
        var y = (double)worldPosition.y * KilometersPerWorldUnit + GroundRadiusKilometers + WorldOriginAltitudeKilometers;
        var z = (double)worldPosition.z * KilometersPerWorldUnit;
        var radius = System.Math.Sqrt(x * x + y * y + z * z);
        if (!double.IsFinite(radius) || radius < GroundRadiusKilometers) {
            throw new ArgumentOutOfRangeException(nameof(worldPosition));
        }
        var sunLength = System.Math.Sqrt((double)SunDirection.x * SunDirection.x
            + (double)SunDirection.y * SunDirection.y + (double)SunDirection.z * SunDirection.z);
        var sx = SunDirection.x / sunLength;
        var sy = SunDirection.y / sunLength;
        var sz = SunDirection.z / sunLength;
        var b = x * sx + y * sy + z * sz;
        var groundDiscriminant = b * b - (radius - GroundRadiusKilometers) * (radius + GroundRadiusKilometers);
        if (b < 0 && groundDiscriminant >= 0) { return float3.zero; }
        var top = (double)GroundRadiusKilometers + AtmosphereHeightKilometers;
        var topDiscriminant = b * b - (radius - top) * (radius + top);
        if (topDiscriminant <= 0) { return SolarIrradiance; }
        var root = System.Math.Sqrt(topDiscriminant);
        var start = System.Math.Max(-b - root, 0);
        var end = -b + root;
        if (end <= start) { return SolarIrradiance; }
        var rayleighDepth = 0.0;
        var mieDepth = 0.0;
        var ozoneDepth = 0.0;
        var step = (end - start) / 256;
        for (var i = 0; i < 256; i++) {
            var t = start + (i + 0.5) * step;
            var px = x + sx * t;
            var py = y + sy * t;
            var pz = z + sz * t;
            var altitude = System.Math.Max(System.Math.Sqrt(px * px + py * py + pz * pz) - GroundRadiusKilometers, 0);
            rayleighDepth += System.Math.Exp(-altitude / RayleighScaleHeightKilometers) * step;
            mieDepth += System.Math.Exp(-altitude / MieScaleHeightKilometers) * step;
            ozoneDepth += System.Math.Max(0, 1 - System.Math.Abs(altitude - OzoneCenterKilometers) / OzoneHalfWidthKilometers) * step;
        }
        var opticalDepth = RayleighScatteringPerKilometer * (float)rayleighDepth
            + (MieScatteringPerKilometer + MieAbsorptionPerKilometer) * (float)mieDepth
            + OzoneAbsorptionPerKilometer * (float)ozoneDepth;
        return SolarIrradiance * new float3(MathF.Exp(-opticalDepth.x), MathF.Exp(-opticalDepth.y), MathF.Exp(-opticalDepth.z));
    }
}
