using Sia.Math;

namespace Sia.Engine.Rendering.Pbr;

// One immutable RGBA32Uint table. All offsets are texels, all node indices are
// preorder ordinals. Escape links make chart queries stackless and bounded.
internal sealed partial class PbrLightmapMetadata
{
    internal const int MaximumInstances = 1_000_000;
    internal uint[] Words { get; }
    internal int Width { get; }
    internal int Height { get; }
    internal ulong Bytes => (ulong)Words.Length * sizeof(uint);

    internal static ulong RequiredBytes(PbrLightmapData index, int instances)
    {
        ArgumentNullException.ThrowIfNull(index);
        return RequiredBytes(index.Receivers.Length, index.Charts.Length, instances);
    }

    internal static ulong RequiredBytes(int receivers, int charts, int instances)
    {
        if (receivers is < 1 or > PbrLightmapData.MaximumReceivers || charts < receivers || charts > PbrLightmapData.MaximumCharts)
            throw new ArgumentOutOfRangeException(nameof(receivers));
        if (instances is < 1 or > MaximumInstances) throw new ArgumentOutOfRangeException(nameof(instances));
        var texels = checked(2 + (instances + 3) / 4 + receivers * 4 + charts * 4);
        var width = System.Math.Min(256, texels);
        return checked((ulong)width * (ulong)((texels + width - 1) / width) * 16);
    }

    // Budget covers padded output plus the reusable chart-index sort scratch.
    internal PbrLightmapMetadata(PbrLightmapData index, ReadOnlySpan<int> receivers, ulong maximumBytes,
        CancellationToken cancellationToken = default)
    {
        var bytes = RequiredBytes(index, receivers.Length);
        var scratchCount = 0;
        foreach (var receiver in index.Receivers) scratchCount = System.Math.Max(scratchCount, receiver.ChartCount);
        if (checked(bytes + (ulong)scratchCount * sizeof(int)) > maximumBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes), "Lightmap metadata and chart scratch exceed CPU admission.");
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var receiver in receivers)
            if (receiver < -1 || receiver >= index.Receivers.Length) throw new ArgumentException("Invalid receiver row.", nameof(receivers));

        var texels = 2 + (receivers.Length + 3) / 4 + index.Receivers.Length * 4 + index.Charts.Length * 4;
        Width = System.Math.Min(256, texels); Height = (texels + Width - 1) / Width;
        Words = new uint[Width * Height * 4];
        var drawStart = 2; var receiverStart = drawStart + (receivers.Length + 3) / 4;
        var chartStart = receiverStart + index.Receivers.Length * 5;
        var nodeStart = chartStart + index.Charts.Length * 2;
        Write(0, (uint)receivers.Length, (uint)index.Receivers.Length, (uint)index.Charts.Length, (uint)index.Pages.Length);
        Write(1, (uint)drawStart, (uint)receiverStart, (uint)chartStart, (uint)nodeStart);
        for (var at = 0; at < receivers.Length; at++) {
            if ((at & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
            Words[drawStart * 4 + at] = (uint)(receivers[at] + 1);
        }
        var order = new int[scratchCount]; var node = 0; var page = 0;
        var horizontalOrder = Comparer<int>.Create((a, b) => Compare(a, b, true));
        var verticalOrder = Comparer<int>.Create((a, b) => Compare(a, b, false));
        int Compare(int a, int b, bool horizontal)
        {
            var ca = index.Charts[a]; var cb = index.Charts[b];
            var difference = horizontal ? (ca.X * 2 + ca.Width).CompareTo(cb.X * 2 + cb.Width)
                : (ca.Y * 2 + ca.Height).CompareTo(cb.Y * 2 + cb.Height);
            return difference != 0 ? difference : a.CompareTo(b);
        }
        for (var r = 0; r < index.Receivers.Length; r++) {
            cancellationToken.ThrowIfCancellationRequested(); var receiver = index.Receivers[r];
            var endNode = node + receiver.ChartCount * 2 - 1;
            Write(receiverStart + r * 5, (uint)receiver.Resolution, (uint)node, (uint)endNode, (uint)page);
            Float(receiverStart + r * 5 + 1, receiver.Quantization.Constant);
            Words[(receiverStart + r * 5 + 1) * 4 + 3] = (uint)receiver.FirstMip;
            Float(receiverStart + r * 5 + 2, receiver.Quantization.Y);
            Float(receiverStart + r * 5 + 3, receiver.Quantization.Z);
            Float(receiverStart + r * 5 + 4, receiver.Quantization.X);
            for (var local = 0; local < receiver.ChartCount; local++) {
                var at = receiver.FirstChart + local; order[local] = at;
                var value = index.Charts[at].Coarse;
                receiver.Quantization.CopyPackedCoefficients(value,Words.AsSpan((chartStart+at*2)*4,8));
            }
            Build(0, receiver.ChartCount);
            for (var dimension = receiver.Resolution >> receiver.FirstMip; dimension >= 2; dimension >>= 1) {
                var grid = (dimension + 63) / 64; page += grid * grid;
            }

            void Build(int first, int count)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var x = int.MaxValue; var y = int.MaxValue; var right = 0; var bottom = 0;
                for (var at = first; at < first + count; at++) {
                    var chart = index.Charts[order[at]];
                    x = System.Math.Min(x, chart.X); y = System.Math.Min(y, chart.Y);
                    right = System.Math.Max(right, chart.X + chart.Width); bottom = System.Math.Max(bottom, chart.Y + chart.Height);
                }
                var current = node++; var escape = current + count * 2 - 1;
                // Accepted receiver coordinates are 0..8192, exactly representable
                // in UInt16. Escape/chart indices retain their full UInt32 range.
                Write(nodeStart+current,(uint)x|((uint)y<<16),(uint)right|((uint)bottom<<16),
                    (uint)escape,count==1?(uint)order[first]+1:0);
                if (count == 1) return;
                var horizontal = right - x >= bottom - y;
                Array.Sort(order, first, count, horizontal ? horizontalOrder : verticalOrder);
                var half = count / 2; Build(first, half); Build(first + half, count - half);
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    private void Write(int texel, uint x, uint y, uint z, uint w)
    { var at = texel * 4; Words[at] = x; Words[at + 1] = y; Words[at + 2] = z; Words[at + 3] = w; }

    private void Float(int texel, float3 value) => Write(texel, BitConverter.SingleToUInt32Bits(value.x),
        BitConverter.SingleToUInt32Bits(value.y), BitConverter.SingleToUInt32Bits(value.z), 0);
}
