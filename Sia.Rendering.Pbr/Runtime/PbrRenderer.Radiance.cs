using Sia.Math;

namespace Sia.Engine.Rendering.Pbr;

public sealed partial class PbrRenderer
{
    // Static shading inputs are immutable through this renderer's lifetime. This
    // admission uses declared receiver ranges, never forces page IO or residency.
    private float StaticRadianceScale(float minimum, ReadOnlySpan<PbrMaterialAsset> materials)
    {
        double specular = 0, irradiance = 0, lutX = 1, lutY = 0;
        if (Settings.BakedEnvironment is { } environment) {
            specular = CubePeak(environment);
            irradiance = ShPeak(environment.Coefficients.Span);
            lutX = lutY = 0;
            var lut = environment.BrdfLut.Span;
            for (var at = 0; at < lut.Length; at += 4) {
                lutX = System.Math.Max(lutX, System.Math.Abs((float)lut[at]));
                lutY = System.Math.Max(lutY, System.Math.Abs((float)lut[at + 1]));
            }
        }
        foreach (var capture in Settings.ReflectionCaptures.Span)
            specular = System.Math.Max(specular, CubePeak(capture.Environment));
        if (Settings.BakedProbes is { } probes)
            irradiance = System.Math.Max(irradiance, ShPeak(probes.Coefficients.Span));
        if (Settings.Lightmaps is { } lightmaps) foreach (var receiver in lightmaps.Set.Receivers) {
            var q = receiver.Receiver.Quantization;
            for (var c = 0; c < 3; c++) {
                var first = (double)q.Y[c]; var second = (double)q.Z[c]; var third = (double)q.X[c];
                var bound = short.MaxValue * (.886228 * q.Constant[c]
                    + 1.023328 * System.Math.Sqrt(first * first + second * second + third * third));
                irradiance = System.Math.Max(irradiance, bound);
            }
        }
        double emission = 0, diffuseWeight = 0, specularWeight = 0;
        foreach (var asset in materials) {
            var p = asset.Parameters;
            double basePeak = 0;
            var physicalBase = math.all(p.BaseColor >= 0) && math.all(p.BaseColor <= 1);
            for (var c = 0; c < 3; c++) {
                basePeak = System.Math.Max(basePeak, System.Math.Abs((double)p.BaseColor[c]));
                var emitted = (double)p.EmissiveColor[c] * p.EmissiveStrength;
                if (!double.IsFinite(emitted) || System.Math.Abs(emitted) > float.MaxValue)
                    throw new ArgumentException("Material emission exceeds finite Float32 shading admission.");
                emission = System.Math.Max(emission, System.Math.Abs(emitted));
            }
            var metal = asset.MetallicRoughness is null ? p.Metallic : 0;
            diffuseWeight = System.Math.Max(diffuseWeight,
                basePeak * (physicalBase ? 1 : 1 + System.Math.Max(1, basePeak)) * (1 - metal));
            var f0 = System.Math.Max(.04, .04 * (1 - p.Metallic) + basePeak * p.Metallic);
            specularWeight = System.Math.Max(specularWeight, f0 * lutX + lutY);
        }
        var peak = emission + diffuseWeight * irradiance / System.Math.PI + specularWeight * specular;
        if (!double.IsFinite(peak) || peak > float.MaxValue / 4096.0
            || diffuseWeight > float.MaxValue / 4096.0 || specularWeight > float.MaxValue / 4096.0)
            throw new ArgumentException("Static PBR shading exceeds finite Float32 working admission.");
        // Keep one bit of storage headroom for filtering and finite arithmetic.
        // The source representation's scale remains a lower bound.
        while (peak / minimum > 32752) {
            if (minimum >= MathF.ScaleB(1, 100))
                throw new ArgumentException("Static PBR shading exceeds the HDR attachment scale range.");
            minimum *= 2;
        }
        return minimum;
    }

    private static double CubePeak(IblEnvironmentAsset environment)
    {
        var cube = environment.Cube.Span; double peak = 0;
        for (var at = 0; at < cube.Length; at += 4)
            for (var c = 0; c < 3; c++) peak = System.Math.Max(peak, System.Math.Abs((double)(float)cube[at + c] * environment.CubeRadianceScale));
        return peak;
    }

    private static double ShPeak(ReadOnlySpan<float4> coefficients)
    {
        double peak = 0;
        for (var at = 0; at < coefficients.Length; at += 9) for (var c = 0; c < 3; c++) {
            var y = (double)coefficients[at + 1][c]; var z = (double)coefficients[at + 2][c]; var x = (double)coefficients[at + 3][c];
            var bound = .886228 * System.Math.Abs(coefficients[at][c]) + 1.023328 * System.Math.Sqrt(y * y + z * z + x * x)
                + .429044 * (System.Math.Abs(coefficients[at + 4][c]) + System.Math.Abs(coefficients[at + 5][c])
                    + System.Math.Abs(coefficients[at + 7][c]) + System.Math.Abs(coefficients[at + 8][c]))
                + .495417 * System.Math.Abs(coefficients[at + 6][c]);
            peak = System.Math.Max(peak, bound);
        }
        return peak;
    }
}
