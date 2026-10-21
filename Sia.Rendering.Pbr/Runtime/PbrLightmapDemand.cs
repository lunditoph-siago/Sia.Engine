namespace Sia.Engine.Rendering.Pbr;

// Conservative projected receiver demand. Finest requested mip and maximum
// projected extent are shared across all section instances and views in a frame.
internal sealed class PbrLightmapDemand
{
    private readonly PbrLightmapSetData _index;
    private readonly int[] _rows, _firstPages;
    private readonly float[] _pixels;
    internal static ulong RequiredBytes(PbrLightmapSetData index, int rows)
    {
        ArgumentNullException.ThrowIfNull(index);
        if (rows is < 1 or > PbrLightmapMetadata.MaximumInstances) throw new ArgumentOutOfRangeException(nameof(rows));
        return checked((ulong)rows * 4 + (ulong)index.Receivers.Length * 8);
    }

    internal PbrLightmapDemand(PbrLightmapSetData index, ReadOnlySpan<int> rows)
    {
        _ = RequiredBytes(index, rows.Length);
        foreach (var row in rows) if (row < -1 || row >= index.Receivers.Length) throw new ArgumentException("Invalid receiver row.", nameof(rows));
        _index = index; _rows = rows.ToArray(); _pixels = new float[index.Receivers.Length];
        _firstPages = new int[index.Receivers.Length]; var pages = new int[index.Products.Length];
        foreach (var r in Enumerable.Range(0, _firstPages.Length).OrderBy(r => index.Receivers[r].Product)
            .ThenBy(r => index.Receivers[r].ProductReceiver)) {
            var product = index.Receivers[r].Product; _firstPages[r] = pages[product];
            var receiver = index.Receivers[r].Receiver;
            for (var dimension = receiver.Resolution >> receiver.FirstMip; dimension >= 2; dimension >>= 1) {
                var grid = (dimension + 63) / 64; pages[product] += grid * grid;
            }
        }
    }

    internal void BeginFrame() => Array.Clear(_pixels);

    internal void Demand(uint row, float pixels)
    {
        if (row >= _rows.Length || !float.IsFinite(pixels) || pixels < 0) throw new ArgumentException("Invalid receiver demand.");
        var receiver = _rows[row];
        if (receiver >= 0) _pixels[receiver] = MathF.Max(_pixels[receiver], pixels);
    }

    internal void ApplyProducts(Action<int, float> demand)
    {
        for (var r = 0; r < _pixels.Length; r++) if (_pixels[r] > 1) demand(_index.Receivers[r].Product, _pixels[r]);
    }

    internal void ApplyPages(Action<int, int, float> demand)
    {
        for (var r = 0; r < _pixels.Length; r++) {
            var pixels = _pixels[r]; if (pixels <= 0) continue;
            var receiver = _index.Receivers[r].Receiver;
            var resolution = receiver.Resolution;
            var level = (int)MathF.Max(0, MathF.Floor(MathF.Log2(resolution / MathF.Max(1, pixels))));
            var first = _firstPages[r];
            for (var mip = receiver.FirstMip; (resolution >> mip) >= 2; mip++) {
                var dimension = resolution >> mip; var grid = (dimension + 63) / 64; var count = grid * grid;
                // Coarse pages remain cheap, higher-priority support for incomplete fine grids.
                if (mip >= level) for (var page = 0; page < count; page++) demand(_index.Receivers[r].Product, first + page, pixels / count);
                first += count;
            }
        }
    }
}
