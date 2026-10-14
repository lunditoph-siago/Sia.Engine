using System.Runtime.CompilerServices;
using Sia.Math;

namespace Sia.Engine.Rendering.Pbr;

/// <summary>Linear incident-radiance SH, ordered constant, Y, Z, X. Coverage is independent of energy.</summary>
public readonly record struct PbrLightmapCoefficients(float3 Constant, float3 Y, float3 Z, float3 X, bool Covered)
{
    public float3 EvaluateIrradiance(float3 normal)
        => math.max(float3.zero, EvaluateSignedIrradiance(normal));

    /// <summary>Cosine-convolved signed radiance. Add corrections to the bake before clipping.</summary>
    public float3 EvaluateSignedIrradiance(float3 normal)
    {
        var length = math.lengthsq(normal);
        if (!math.all(math.isfinite(normal)) || !float.IsFinite(length) || length <= 0)
            throw new ArgumentException("A finite, nonzero surface normal is required.", nameof(normal));
        if (!Covered) return default;
        normal /= MathF.Sqrt(length);
        // Cosine convolution: pi for l=0, 2*pi/3 for l=1. Direct lights remain live.
        return Constant * .886226925f
            + (Y * normal.y + Z * normal.z + X * normal.x) * 1.02332671f;
    }

    /// <summary>Missing members yield no correction; a valid black pair remains covered.</summary>
    public static PbrLightmapCoefficients Difference(PbrLightmapCoefficients live, PbrLightmapCoefficients reference)
    {
        if (!live.Covered || !reference.Covered) return default;
        var difference = new PbrLightmapCoefficients(live.Constant - reference.Constant, live.Y - reference.Y,
            live.Z - reference.Z, live.X - reference.X, true);
        for (var component = 0; component < 4; component++)
            if (!math.all(math.isfinite(live.Component(component))) || !math.all(math.isfinite(reference.Component(component)))
                || !math.all(math.isfinite(difference.Component(component))))
                throw new ArgumentException("A complete transport pair requires finite signed coefficients.");
        return difference;
    }

    internal float3 Component(int index) => index switch {
        0 => Constant, 1 => Y, 2 => Z, 3 => X, _ => throw new ArgumentOutOfRangeException(nameof(index))
    };
}

/// <summary>
/// One independently owned, quantized square lightmap page. No scene, transport, World or GPU ownership.
/// Each coefficient/color channel has its own scale; uncovered samples always have zero coefficients.
/// </summary>
public sealed partial class PbrLightmapPageData
{
    public const int MaximumEdge = 64;
    public const int MaximumEncodedBytes = 92 + MaximumEdge * MaximumEdge * 25;
    public const float MaximumCoefficient = 1e20f;
    private readonly float3[] _scales;
    private readonly short[] _coefficients;
    private readonly byte[] _coverage;
    public int Edge { get; }
    public int TexelCount => Edge * Edge;
    public ulong PayloadBytes => (ulong)_coverage.Length + (ulong)_coefficients.Length * sizeof(short)
        + 4ul * (ulong)Unsafe.SizeOf<float3>();
    public int EncodedBytes => 92 + TexelCount * 25;
    public string ContentHash { get; }
    public PbrLightmapQuantization Quantization => new(_scales[0], _scales[1], _scales[2], _scales[3]);

    private PbrLightmapPageData(int edge, float3[] scales, short[] coefficients, byte[] coverage,
        string? contentHash = null, CancellationToken cancellationToken = default)
    {
        Edge = edge; _scales = scales; _coefficients = coefficients; _coverage = coverage;
        Validate();
        ContentHash = contentHash ?? Write(Stream.Null, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// Snapshots one page under the caller's measured receiver scales, or measures a standalone page.
    /// Out-of-range values are rejected rather than clipped. The budget covers owned array elements.
    /// </summary>
    public static PbrLightmapPageData Create(int edge, ReadOnlySpan<PbrLightmapCoefficients> texels,
        ulong maximumBytes = MaximumEncodedBytes, CancellationToken cancellationToken = default,
        PbrLightmapQuantization? quantization = null)
    {
        Admit(edge, maximumBytes); cancellationToken.ThrowIfCancellationRequested();
        if (texels.Length != edge * edge) throw new ArgumentException("Lightmap page shape differs from its samples.", nameof(texels));
        var measured = quantization ?? default(PbrLightmapQuantization).Include(texels, cancellationToken);
        measured.Validate();
        var scales = new float3[4];
        for (var component = 0; component < 4; component++) scales[component] = measured.Component(component);
        for (var at = 0; at < texels.Length; at++) {
            if ((at & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            var sample = texels[at];
            for (var component = 0; component < 4; component++) {
                var value = sample.Component(component);
                PbrLightmapQuantization.ValidateCoefficient(value, sample.Covered);
                for (var channel = 0; channel < 3; channel++)
                    if (MathF.Abs(value[channel]) > (float)((double)scales[component][channel] * short.MaxValue))
                        throw new ArgumentException("Lightmap coefficient exceeds the measured receiver range.", nameof(quantization));
            }
        }
        var coefficients = new short[texels.Length * 12]; var coverage = new byte[texels.Length];
        for (var at = 0; at < texels.Length; at++) {
            if ((at & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            coverage[at] = texels[at].Covered ? (byte)1 : (byte)0;
            for (var component = 0; component < 4; component++)
                for (var channel = 0; channel < 3; channel++) {
                    var value = texels[at].Component(component)[channel];
                    coefficients[at * 12 + component * 3 + channel] = measured.Encode(component, channel, value);
                }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(edge, scales, coefficients, coverage, cancellationToken: cancellationToken);
    }

    public PbrLightmapCoefficients Texel(int x, int y)
    {
        if ((uint)x >= Edge || (uint)y >= Edge) throw new ArgumentOutOfRangeException(nameof(x));
        var at = y * Edge + x; var quantization = Quantization;
        float3 Decode(int component) => new(
            quantization.Decode(component, 0, _coefficients[at * 12 + component * 3]),
            quantization.Decode(component, 1, _coefficients[at * 12 + component * 3 + 1]),
            quantization.Decode(component, 2, _coefficients[at * 12 + component * 3 + 2]));
        return new(Decode(0), Decode(1), Decode(2), Decode(3), _coverage[at] != 0);
    }

    /// <summary>Page-local bilinear sampling, renormalizing only covered taps; borders belong to the producer.</summary>
    public PbrLightmapCoefficients Sample(float2 uv)
    {
        if (!math.all(math.isfinite(uv))) throw new ArgumentException("Lightmap coordinates must be finite.", nameof(uv));
        var position = math.clamp(uv, float2.zero, new float2(1)) * Edge - new float2(.5f);
        var x = (int)MathF.Floor(position.x); var y = (int)MathF.Floor(position.y);
        var fraction = position - new float2(x, y);
        float3 constant = default, cy = default, cz = default, cx = default; var weight = 0f;
        for (var dy = 0; dy < 2; dy++)
            for (var dx = 0; dx < 2; dx++) {
                var tap = Texel(System.Math.Clamp(x + dx, 0, Edge - 1), System.Math.Clamp(y + dy, 0, Edge - 1));
                var w = (dx == 0 ? 1 - fraction.x : fraction.x) * (dy == 0 ? 1 - fraction.y : fraction.y);
                if (!tap.Covered || w <= 0) continue;
                weight += w; constant += tap.Constant * w; cy += tap.Y * w; cz += tap.Z * w; cx += tap.X * w;
            }
        return weight <= 0 ? default : new(constant / weight, cy / weight, cz / weight, cx / weight, true);
    }

    private static void Admit(int edge, ulong maximumBytes)
    {
        if (edge is < 2 or > MaximumEdge || (edge & (edge - 1)) != 0)
            throw new ArgumentException("Lightmap page edge must be a power of two between 2 and 64.", nameof(edge));
        var encoded = 92ul + (ulong)edge * (ulong)edge * 25;
        var owned = (ulong)edge * (ulong)edge * 25 + 4ul * (ulong)Unsafe.SizeOf<float3>();
        if (encoded > maximumBytes || owned > maximumBytes)
            throw new ArgumentException("Lightmap page exceeds its configured encoded/owned-array budget.", nameof(maximumBytes));
    }

    private void Validate()
    {
        if (_scales.Length != 4 || _coverage.Length != TexelCount || _coefficients.Length != TexelCount * 12)
            throw new ArgumentException("Invalid lightmap page arrays.");
        Quantization.Validate();
        for (var at = 0; at < TexelCount; at++) {
            if (_coverage[at] > 1) throw new ArgumentException("Invalid lightmap coverage marker.");
            for (var component = 0; component < 4; component++)
                for (var channel = 0; channel < 3; channel++) {
                    var value = _coefficients[at * 12 + component * 3 + channel];
                    if (value == short.MinValue || ((_coverage[at] == 0 || _scales[component][channel] == 0) && value != 0))
                        throw new ArgumentException("Invalid lightmap coefficient encoding.");
                }
        }
    }
}
