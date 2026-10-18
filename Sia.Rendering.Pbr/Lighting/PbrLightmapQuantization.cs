using Sia.Math;

namespace Sia.Engine.Rendering.Pbr;

/// <summary>
/// One receiver's signed L1 quantization scales, independent for every coefficient and RGB channel.
/// The offline caller measures all fine/coarse values in bounded batches before encoding pages.
/// </summary>
public readonly record struct PbrLightmapQuantization(float3 Constant, float3 Y, float3 Z, float3 X)
{
    /// <summary>Writes the shared two-Uint4 rendering ABI: twelve signed16 codes, coverage, then zero padding.</summary>
    public void CopyPackedCoefficients(PbrLightmapCoefficients value,Span<uint> destination,CancellationToken cancellationToken=default)
    {
        if (destination.Length!=8) throw new ArgumentException("One coefficient value requires eight packed words.",nameof(destination));
        cancellationToken.ThrowIfCancellationRequested(); Validate();
        Span<uint> words=stackalloc uint[8]; words.Clear();
        for (var component=0;component<4;component++) ValidateCoefficient(value.Component(component),value.Covered);
        for (var pair=0;pair<6;pair++) {
            var first=pair*2; var second=first+1;
            var lo=Encode(first/3,first%3,value.Component(first/3)[first%3]);
            var hi=Encode(second/3,second%3,value.Component(second/3)[second%3]);
            words[pair]=(uint)(ushort)lo|((uint)(ushort)hi<<16);
        }
        words[6]=value.Covered?1u:0u; words.CopyTo(destination);
    }

    /// <summary>Returns scales enclosing the previous measurement and this batch; owns no scratch or IO.</summary>
    public PbrLightmapQuantization Include(ReadOnlySpan<PbrLightmapCoefficients> values,
        CancellationToken cancellationToken = default)
    {
        Validate(); cancellationToken.ThrowIfCancellationRequested();
        Span<float3> scales = stackalloc float3[4];
        for (var component = 0; component < 4; component++) scales[component] = Component(component);
        for (var at = 0; at < values.Length; at++) {
            if ((at & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            for (var component = 0; component < 4; component++) {
                var value = values[at].Component(component);
                ValidateCoefficient(value, values[at].Covered);
                for (var channel = 0; channel < 3; channel++) {
                    var magnitude = MathF.Abs(value[channel]);
                    if (magnitude == 0) continue;
                    var scale = MathF.Max(float.Epsilon, magnitude / short.MaxValue);
                    // Enclose the actual peak rather than accepting clipping caused by rounding down.
                    if ((double)scale * short.MaxValue < magnitude) scale = MathF.BitIncrement(scale);
                    scales[component][channel] = MathF.Max(scales[component][channel], scale);
                }
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(scales[0], scales[1], scales[2], scales[3]);
    }

    internal float3 Component(int index) => index switch {
        0 => Constant, 1 => Y, 2 => Z, 3 => X, _ => throw new ArgumentOutOfRangeException(nameof(index))
    };

    internal void Validate()
    {
        for (var component = 0; component < 4; component++) {
            var scale = Component(component);
            if (!math.all(math.isfinite(scale)) || math.any(scale < 0)
                || math.any(scale > PbrLightmapPageData.MaximumCoefficient / short.MaxValue * 1.000001f))
                throw new ArgumentException("Invalid lightmap coefficient scales.");
        }
    }

    internal short Encode(int component, int channel, float value)
    {
        var scale = Component(component)[channel];
        if (!float.IsFinite(value) || MathF.Abs(value) > (float)((double)scale * short.MaxValue))
            throw new ArgumentException("Lightmap coefficient exceeds the measured receiver range.");
        return scale == 0 ? (short)0 : (short)System.Math.Clamp(System.Math.Round((double)value / scale), -short.MaxValue, short.MaxValue);
    }

    internal float Decode(int component, int channel, short value)
        // Ceiling scales can reconstruct a maximum input a fraction of one float ULP above
        // its domain bound. Keep decoded values admissible by the same bounded linear filter.
        => System.Math.Clamp(Component(component)[channel] * value, -PbrLightmapPageData.MaximumCoefficient, PbrLightmapPageData.MaximumCoefficient);

    internal PbrLightmapCoefficients Quantize(PbrLightmapCoefficients value)
    {
        Span<float3> components = stackalloc float3[4];
        for (var component = 0; component < 4; component++) {
            ValidateCoefficient(value.Component(component), value.Covered);
            for (var channel = 0; channel < 3; channel++)
                components[component][channel] = Decode(component, channel, Encode(component, channel, value.Component(component)[channel]));
        }
        return new(components[0], components[1], components[2], components[3], value.Covered);
    }

    internal static void ValidateCoefficient(float3 value, bool covered)
    {
        if (!math.all(math.isfinite(value)) || math.any(math.abs(value) > PbrLightmapPageData.MaximumCoefficient)
            || (!covered && math.any(value != 0)))
            throw new ArgumentException("Lightmap coefficients must be finite, bounded and zero when uncovered.");
    }
}
