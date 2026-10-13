using System.Runtime.CompilerServices;

namespace Sia.Engine.Rendering.Pbr;

public readonly record struct PbrLightmapTile(int Level, int X, int Y, int Edge);

/// <summary>Borrowed unquantized tile, valid only during the synchronous call. The caller owns storage and rollback.</summary>
public delegate void PbrLightmapTileWriter(PbrLightmapTile tile, ReadOnlySpan<PbrLightmapFilterTexel> texels, CancellationToken cancellationToken);

/// <summary>
/// Offline receiver mip producer. Borrows the sampler/baker; owns a bounded page per hierarchy
/// level and chart reduction scratch, never an entire atlas. IO, staging, metadata reservation,
/// second-pass encoding under returned receiver scales and atomic publication belong to the caller.
/// </summary>
public sealed class PbrLightmapBake
{
    private readonly PbrLightmapSurface _surface;
    private readonly PbrLightmapBaker _baker;
    private readonly PbrLightmapFilterTexel[][] _mips;
    private readonly PbrLightmapSurfaceTexel[] _geometry;
    private readonly PbrLightmapCoefficients[] _raw, _coarse;
    private readonly PbrLightmapChartAccumulator _accumulator;
    private int _busy;
    public ulong PayloadBytes { get; }
    public int TileCount { get; }

    public PbrLightmapBake(PbrLightmapSurface surface, PbrLightmapBaker baker,
        ulong maximumWorkingBytes = 16ul * 1024 * 1024)
    {
        ArgumentNullException.ThrowIfNull(surface); ArgumentNullException.ThrowIfNull(baker);
        if (surface.ChartCount > PbrLightmapChartAccumulator.MaximumCharts)
            throw new ArgumentException("Receiver chart count exceeds coarse metadata limits.", nameof(surface));
        _surface = surface; _baker = baker;
        var levels = 0; var texels = 0;
        for (var dimension = surface.Resolution; dimension >= 2; dimension >>= 1) {
            var edge = System.Math.Min(PbrLightmapPageData.MaximumEdge, dimension);
            levels++; texels += edge * edge; TileCount += (dimension / edge) * (dimension / edge);
        }
        var fine = surface.Edge * surface.Edge;
        PayloadBytes = (ulong)levels * (ulong)IntPtr.Size + (ulong)texels * (ulong)Unsafe.SizeOf<PbrLightmapFilterTexel>()
            + (ulong)fine * (ulong)(Unsafe.SizeOf<PbrLightmapSurfaceTexel>() + Unsafe.SizeOf<PbrLightmapCoefficients>())
            + (ulong)surface.ChartCount * (ulong)(Unsafe.SizeOf<PbrLightmapCoefficients>() + 4 * 24 + sizeof(double));
        if (PayloadBytes > maximumWorkingBytes)
            throw new ArgumentException("Receiver mip/coarse scratch exceeds known-array admission.", nameof(maximumWorkingBytes));
        _mips = new PbrLightmapFilterTexel[levels][];
        for (var level = 0; level < levels; level++) {
            var edge = System.Math.Min(PbrLightmapPageData.MaximumEdge, surface.Resolution >> level);
            _mips[level] = new PbrLightmapFilterTexel[edge * edge];
        }
        _geometry = new PbrLightmapSurfaceTexel[fine]; _raw = new PbrLightmapCoefficients[fine];
        _coarse = new PbrLightmapCoefficients[surface.ChartCount];
        _accumulator = new(surface.ChartCount, maximumWorkingBytes);
    }

    /// <summary>
    /// Computes every fine/mip tile once and emits levels at or above firstMip, then copies intrinsic coefficients in
    /// source-chart order and returns receiver scales enclosing fine/mip/coarse values. The caller
    /// stages tiles during the callback and encodes them after these scales are known.
    /// Fine geometry integration, quantization and chart support are independent of the storage cap. On failure,
    /// discard staged output; coarseOutput is unchanged and this producer can be retried serially.
    /// </summary>
    public PbrLightmapQuantization Run(PbrLightmapTileWriter write, Span<PbrLightmapCoefficients> coarseOutput,out PbrLightmapCoefficients support,
        CancellationToken cancellationToken = default, int firstMip = 0)
    {
        support=default; ArgumentNullException.ThrowIfNull(write);
        if (firstMip < 0 || firstMip >= _mips.Length) throw new ArgumentOutOfRangeException(nameof(firstMip));
        if (coarseOutput.Length != _coarse.Length) throw new ArgumentException("Coarse output must match source chart count.", nameof(coarseOutput));
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.Exchange(ref _busy, 1) != 0) throw new InvalidOperationException("Receiver production must be serialized.");
        try {
            _accumulator.Clear(); var quantization = default(PbrLightmapQuantization);
            void Emit(PbrLightmapTile tile, ReadOnlySpan<PbrLightmapFilterTexel> texels, CancellationToken token)
            {
                if (tile.Level >= firstMip) write(tile, texels, token);
            }
            Produce(_mips.Length - 1, 0, 0, Emit, ref quantization, cancellationToken);
            _accumulator.CopyTo(_coarse); quantization = quantization.Include(_coarse, cancellationToken);
            var average=_accumulator.Average(cancellationToken); quantization=quantization.Include([average],cancellationToken);
            cancellationToken.ThrowIfCancellationRequested(); _coarse.AsSpan().CopyTo(coarseOutput); support=average;
            return quantization;
        }
        finally { Volatile.Write(ref _busy, 0); }
    }

    private void Produce(int level, int pageX, int pageY, PbrLightmapTileWriter write,
        ref PbrLightmapQuantization quantization, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var edge = System.Math.Min(PbrLightmapPageData.MaximumEdge, _surface.Resolution >> level);
        var target = _mips[level];
        if (level == 0) {
            _surface.FillPage(pageX, pageY, _geometry, token); _baker.BakePage(edge, _geometry, _raw, token);
            for (var at = 0; at < target.Length; at++) target[at] = new(_raw[at], _geometry[at].Chart, _geometry[at].AreaWeight);
            _accumulator.Add(target, token);
        }
        else {
            var childDimension = _surface.Resolution >> (level - 1);
            var childEdge = System.Math.Min(PbrLightmapPageData.MaximumEdge, childDimension);
            var quadrants = childDimension > PbrLightmapPageData.MaximumEdge ? 2 : 1;
            var reduced = childEdge / 2;
            Span<PbrLightmapFilterTexel> block = stackalloc PbrLightmapFilterTexel[4];
            for (var dy = 0; dy < quadrants; dy++)
                for (var dx = 0; dx < quadrants; dx++) {
                    Produce(level - 1, pageX * quadrants + dx, pageY * quadrants + dy, write, ref quantization, token);
                    var child = _mips[level - 1];
                    for (var y = 0; y < reduced; y++) {
                        token.ThrowIfCancellationRequested();
                        for (var x = 0; x < reduced; x++) {
                            var at = y * 2 * childEdge + x * 2;
                            block[0] = child[at]; block[1] = child[at + 1]; block[2] = child[at + childEdge]; block[3] = child[at + childEdge + 1];
                            target[(dy * reduced + y) * edge + dx * reduced + x] = PbrLightmapFilter.Reduce2x2(block);
                        }
                    }
                }
        }
        // Include before giving the borrowed buffer to the writer; the buffer is reused after return.
        for (var start = 0; start < target.Length; start += _raw.Length) {
            var count = System.Math.Min(_raw.Length, target.Length - start);
            for (var at = 0; at < count; at++) _raw[at] = target[start + at].Coefficients;
            quantization = quantization.Include(_raw.AsSpan(0, count), token);
        }
        token.ThrowIfCancellationRequested(); write(new(level, pageX * edge, pageY * edge, edge), target, token);
    }
}
