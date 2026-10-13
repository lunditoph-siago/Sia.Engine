using Sia.Math;

namespace Sia.Engine.Rendering.Pbr;

/// <summary>Transient illumination and ownership used by the offline reducer, not geometry or a wire format.</summary>
public readonly record struct PbrLightmapFilterTexel(PbrLightmapCoefficients Coefficients, int Chart, double AreaWeight);

public static class PbrLightmapFilter
{
    /// <summary>
    /// Reduces exactly four children without mixing chart ownership. A block crossing charts is
    /// uncovered; the consumer uses intrinsic chart lighting there. Physical samples use area
    /// weights. Only a block consisting entirely of filtering-border samples uses equal weights.
    /// </summary>
    public static PbrLightmapFilterTexel Reduce2x2(ReadOnlySpan<PbrLightmapFilterTexel> children)
    {
        if (children.Length != 4) throw new ArgumentException("A mip block requires four children.", nameof(children));
        var chart = -1; var mixed = false; var area = 0d; var covered = 0;
        foreach (var child in children) {
            Validate(child);
            if (!child.Coefficients.Covered) continue;
            if (chart < 0) chart = child.Chart; else mixed |= chart != child.Chart;
            area += child.AreaWeight; covered++;
        }
        if (mixed || covered == 0) return new(default, -1, 0);
        double3 constant = default, y = default, z = default, x = default;
        foreach (var child in children) {
            if (!child.Coefficients.Covered) continue;
            var weight = area > 0 ? child.AreaWeight / area : 1d / covered;
            constant += (double3)child.Coefficients.Constant * weight;
            y += (double3)child.Coefficients.Y * weight; z += (double3)child.Coefficients.Z * weight; x += (double3)child.Coefficients.X * weight;
        }
        return new(new((float3)constant, (float3)y, (float3)z, (float3)x, true), chart, area);
    }

    internal static void Validate(PbrLightmapFilterTexel texel)
    {
        if (!double.IsFinite(texel.AreaWeight) || texel.AreaWeight is < 0 or > 1e80
            || (texel.Coefficients.Covered ? texel.Chart < 0 : texel.AreaWeight != 0))
            throw new ArgumentException("Invalid filtering chart or physical area.");
        for (var component = 0; component < 4; component++) {
            var value = texel.Coefficients.Component(component);
            if (!math.all(math.isfinite(value)) || math.any(math.abs(value) > PbrLightmapPageData.MaximumCoefficient)
                || (!texel.Coefficients.Covered && math.any(value != 0)))
                throw new ArgumentException("Filtering coefficients require finite bounded energy and explicit coverage.");
        }
    }
}

/// <summary>
/// Serial bounded accumulator for intrinsic chart lighting over fine pages. Owns only weighted
/// coefficient sums and physical areas. Callers submit each fine texel once and own output buffers.
/// Filtering borders and empty pixels never contribute to the chart's physical surface average.
/// </summary>
public sealed class PbrLightmapChartAccumulator
{
    public const int MaximumCharts = 65536;
    public const int MaximumTexels = 8192 * 8192;
    private readonly double3[] _sums;
    private readonly double[] _areas;
    private int _texels;
    public int ChartCount => _areas.Length;
    public ulong PayloadBytes => (ulong)ChartCount * (4 * 24 + sizeof(double));

    public PbrLightmapChartAccumulator(int chartCount, ulong maximumBytes = 8ul * 1024 * 1024)
    {
        if (chartCount is < 1 or > MaximumCharts) throw new ArgumentOutOfRangeException(nameof(chartCount));
        if ((ulong)chartCount * (4 * 24 + sizeof(double)) > maximumBytes)
            throw new ArgumentException("Chart lighting exceeds known-array admission.", nameof(maximumBytes));
        _sums = new double3[chartCount * 4]; _areas = new double[chartCount];
    }

    public void Clear()
    {
        Array.Clear(_sums); Array.Clear(_areas); _texels = 0;
    }

    /// <summary>Validates one bounded page before changing sums; pre-admission and cancellation leave sums intact.</summary>
    public void Add(ReadOnlySpan<PbrLightmapFilterTexel> texels, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (texels.Length > PbrLightmapPageData.MaximumEdge * PbrLightmapPageData.MaximumEdge
            || texels.Length > MaximumTexels - _texels)
            throw new ArgumentException("Chart accumulation exceeds its bounded page/receiver work limit.", nameof(texels));
        for (var at = 0; at < texels.Length; at++) {
            if ((at & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            var texel = texels[at]; PbrLightmapFilter.Validate(texel);
            if (texel.Coefficients.Covered && texel.Chart >= ChartCount)
                throw new ArgumentException("Lighting texel refers outside the admitted chart set.", nameof(texels));
        }
        cancellationToken.ThrowIfCancellationRequested();
        // At most 4096 additions. No throwing operation or cancellation boundary after mutation begins.
        foreach (var texel in texels) {
            if (texel.AreaWeight == 0) continue;
            _areas[texel.Chart] += texel.AreaWeight;
            for (var component = 0; component < 4; component++)
                _sums[texel.Chart * 4 + component] += (double3)texel.Coefficients.Component(component) * texel.AreaWeight;
        }
        _texels += texels.Length;
    }

    /// <summary>Copies coarse SH values in chart order. A physically covered black chart remains covered.</summary>
    public void CopyTo(Span<PbrLightmapCoefficients> output)
    {
        if (output.Length != ChartCount) throw new ArgumentException("Coarse lighting buffer must match chart count.", nameof(output));
        for (var chart = 0; chart < ChartCount; chart++) {
            var area = _areas[chart]; var at = chart * 4;
            output[chart] = area == 0 ? default : new((float3)(_sums[at] / area), (float3)(_sums[at + 1] / area),
                (float3)(_sums[at + 2] / area), (float3)(_sums[at + 3] / area), true);
        }
    }

    /// <summary>Receiver support weighted by actual sampled surface area; filtering borders contribute no area.</summary>
    public PbrLightmapCoefficients Average(CancellationToken cancellationToken=default)
    {
        var area=0d; Span<double3> sums=stackalloc double3[4]; sums.Clear();
        for (var chart=0;chart<ChartCount;chart++) {
            cancellationToken.ThrowIfCancellationRequested(); area+=_areas[chart];
            for (var component=0;component<4;component++) sums[component]+=_sums[chart*4+component];
        }
        return area==0?default:new((float3)(sums[0]/area),(float3)(sums[1]/area),(float3)(sums[2]/area),(float3)(sums[3]/area),true);
    }
}
