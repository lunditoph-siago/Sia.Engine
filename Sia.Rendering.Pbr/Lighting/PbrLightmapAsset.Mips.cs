using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;

namespace Sia.Engine.Rendering.Pbr;

public sealed partial class PbrLightmapAsset
{
    internal static PbrLightmapChart[] ValidateCharts(int resolution, PbrLightmapReceiver[] receivers,
        ReadOnlySpan<PbrLightmapChart> charts)
    {
        if (charts.IsEmpty) return [];
        if (charts.Length > 1_000_000) throw new ArgumentException("Too many lightmap charts.");
        var allocations = receivers.ToDictionary(r => r.StaticInstance);
        var copy = charts.ToArray();
        var groups = copy.GroupBy(c => c.StaticInstance).ToArray();
        if (groups.Length != receivers.Length) throw new ArgumentException("Each receiver needs chart metadata.");
        var scratch = ArrayPool<byte>.Shared.Rent(receivers.Max(r => r.Resolution * r.Resolution));
        try {
            foreach (var group in groups) {
                if (!allocations.TryGetValue(group.Key, out var receiver)) throw new ArgumentException("Unknown chart receiver.");
                scratch.AsSpan(0, receiver.Resolution * receiver.Resolution).Clear();
                foreach (var chart in group) {
                    if (chart.Width <= 0 || chart.Height <= 0 || chart.X < receiver.X || chart.Y < receiver.Y
                        || chart.Width > receiver.Resolution || chart.Height > receiver.Resolution
                        || chart.X - receiver.X > receiver.Resolution - chart.Width
                        || chart.Y - receiver.Y > receiver.Resolution - chart.Height)
                        throw new ArgumentException("Invalid chart allocation.");
                    for (var y = chart.Y - receiver.Y; y < chart.Y - receiver.Y + chart.Height; y++) {
                        var row = scratch.AsSpan(y * receiver.Resolution + chart.X - receiver.X, chart.Width);
                        if (row.Contains((byte)1)) throw new ArgumentException("Lightmap charts overlap.");
                        row.Fill(1);
                    }
                }
            }
        }
        finally { ArrayPool<byte>.Shared.Return(scratch); }
        return copy;
    }

    // Only levels whose box footprints stay inside the padded rectangle can be sampled.
    internal int ChartMaximumMip(PbrLightmapChart chart) => BitOperations.TrailingZeroCount(
        (uint)(Resolution | chart.X | chart.Y | chart.Width | chart.Height));

    internal static byte[] ChartBakeIdentity(byte[] identity, ReadOnlySpan<PbrLightmapChart> charts)
    {
        if (charts.IsEmpty) return identity;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("SIALM-CHART-BASE-COVERAGE-MIPS1"u8);
        hash.AppendData(identity);
        Span<byte> record = stackalloc byte[20];
        foreach (var chart in charts) {
            BinaryPrimitives.WriteInt32LittleEndian(record, chart.StaticInstance);
            BinaryPrimitives.WriteInt32LittleEndian(record[4..], chart.X);
            BinaryPrimitives.WriteInt32LittleEndian(record[8..], chart.Y);
            BinaryPrimitives.WriteInt32LittleEndian(record[12..], chart.Width);
            BinaryPrimitives.WriteInt32LittleEndian(record[16..], chart.Height);
            hash.AppendData(record);
        }
        return hash.GetHashAndReset();
    }

    // Derive each level from covered base texels, avoiding recursive weighting and
    // a second full atlas. Invalid texels do not darken coefficients; valid black survives.
    internal void WriteMipRows(Span<byte> destination, int mip, int band, int firstRow, int rows)
        => WriteMipRegion(destination, mip, band, 0, firstRow, Resolution >> mip, rows);

    internal void WriteMipRegion(Span<byte> destination, int mip, int band, int firstColumn, int firstRow, int columns, int rows)
    {
        if ((uint)mip >= MipCount || (uint)band >= 4) throw new ArgumentOutOfRangeException(nameof(mip));
        var size = Resolution >> mip;
        var stride = Encoding == PbrLightmapEncoding.L1Unorm8 ? 4 : 8;
        if (firstColumn < 0 || columns < 0 || firstColumn > size - columns
            || firstRow < 0 || rows < 0 || firstRow > size - rows || destination.Length < (long)columns * rows * stride)
            throw new ArgumentOutOfRangeException(nameof(rows));
        var footprint = 1 << mip;
        for (var y = firstRow; y < firstRow + rows; y++)
            for (var x = firstColumn; x < firstColumn + columns; x++) {
                var target = destination.Slice(((y - firstRow) * columns + x - firstColumn) * stride, stride);
                if (mip == 0) {
                    var at = (y * Resolution + x) * 16 + band * 4;
                    if (Encoding == PbrLightmapEncoding.L1Unorm8) QuantizedData.Span.Slice(at, 4).CopyTo(target);
                    else System.Runtime.InteropServices.MemoryMarshal.AsBytes(Data.Span.Slice(at, 4)).CopyTo(target);
                    continue;
                }
                double r = 0, g = 0, b = 0;
                var valid = 0;
                for (var sy = y * footprint; sy < (y + 1) * footprint; sy++)
                    for (var sx = x * footprint; sx < (x + 1) * footprint; sx++) {
                        var at = (sy * Resolution + sx) * 16;
                        if (Encoding == PbrLightmapEncoding.L1Unorm8) {
                            var values = QuantizedData.Span;
                            if (values[at + 3] == 0) continue;
                            at += band * 4;
                            r += values[at]; g += values[at + 1]; b += values[at + 2];
                        } else {
                            var values = Data.Span;
                            if (values[at + 3] == (Half)0) continue;
                            at += band * 4;
                            r += (float)values[at]; g += (float)values[at + 1]; b += (float)values[at + 2];
                        }
                        valid++;
                    }
                target.Clear();
                if (valid == 0) continue;
                if (Encoding == PbrLightmapEncoding.L1Unorm8) {
                    target[0] = (byte)System.Math.Round(r / valid);
                    target[1] = (byte)System.Math.Round(g / valid);
                    target[2] = (byte)System.Math.Round(b / valid);
                    target[3] = band == 0 ? (byte)255 : (byte)0;
                } else {
                    BinaryPrimitives.WriteUInt16LittleEndian(target, BitConverter.HalfToUInt16Bits((Half)(r / valid)));
                    BinaryPrimitives.WriteUInt16LittleEndian(target[2..], BitConverter.HalfToUInt16Bits((Half)(g / valid)));
                    BinaryPrimitives.WriteUInt16LittleEndian(target[4..], BitConverter.HalfToUInt16Bits((Half)(b / valid)));
                    BinaryPrimitives.WriteUInt16LittleEndian(target[6..], BitConverter.HalfToUInt16Bits(band == 0 ? (Half)1 : (Half)0));
                }
            }
    }
}
