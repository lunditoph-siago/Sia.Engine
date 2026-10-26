using System.Buffers.Binary;
using System.Security.Cryptography;
using Sia.Math;
using Sia.WebGPU;

namespace Sia.Engine.Rendering.Pbr;

public sealed partial class PbrDiffuseMoments
{
    /// <summary>
    /// Integrates the finest-LOD diffuse texture functions over uniform UV using borrowed whole
    /// SIATEXR1 streams. Inputs require equal dimensions or constant metallic B,
    /// repeat addressing and unambiguous filtering.
    /// The bound includes both complete encoded inputs and known array elements, not just working pixels.
    /// </summary>
    public static PbrDiffuseMoments Compute(Stream? baseColor, Stream? metallicRoughness,
        ulong maximumBytes = 128ul * 1024 * 1024, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (baseColor is null && metallicRoughness is null) return new(new float3(1), new float3(1), null, null);
        if (baseColor is not null && ReferenceEquals(baseColor, metallicRoughness))
            throw new ArgumentException("Color and linear moment sources require independent borrowed streams.");
        try {
            ulong admitted = 0;
            var color = OpenMomentSource(baseColor, true, maximumBytes, ref admitted, cancellationToken);
            var metal = OpenMomentSource(metallicRoughness, false, maximumBytes, ref admitted, cancellationToken);
            var unequal = color is not null && metal is not null
                && (color.Width != metal.Width || color.Height != metal.Height);
            var shape = color ?? metal!;
            var width = shape.Width; var height = shape.Height; var rowBytes = checked(width * 4);
            var scratchBytes = (int)System.Math.Min(65536ul, System.Math.Max(color?.PayloadBytes ?? 0, metal?.PayloadBytes ?? 0));
            AdmitMomentBytes((ulong)scratchBytes, maximumBytes, ref admitted);
            var scratch = new byte[scratchBytes];
            AuthenticateMomentSource(color, scratch, maximumBytes, ref admitted, cancellationToken);
            AuthenticateMomentSource(metal, scratch, maximumBytes, ref admitted, cancellationToken, unequal);
            if (unequal && metal!.ConstantMetallic is null)
                throw new InvalidDataException("Joint material moments require equal source dimensions or constant metallic B.");

            var colorRow = Array.Empty<byte>();
            var previous = Array.Empty<byte>(); var current = Array.Empty<byte>(); var next = Array.Empty<byte>();
            var linear = Array.Empty<double>();
            if (color is not null) {
                AdmitMomentBytes((ulong)rowBytes + 256 * sizeof(double), maximumBytes, ref admitted);
                colorRow = new byte[rowBytes]; linear = new double[256];
                for (var at = 0; at < linear.Length; at++) {
                    var value = at / 255d;
                    linear[at] = value <= .04045 ? value / 12.92 : System.Math.Pow((value + .055) / 1.055, 2.4);
                }
            }
            if (unequal) {
                var metalRowBytes = checked(metal!.Width * 4);
                AdmitMomentBytes((ulong)metalRowBytes, maximumBytes, ref admitted);
                current = new byte[metalRowBytes];
            }
            else if (metal is not null) {
                AdmitMomentBytes((ulong)rowBytes * 3, maximumBytes, ref admitted);
                previous = new byte[rowBytes]; current = new byte[rowBytes]; next = new byte[rowBytes];
                ReadMomentRow(metal, height - 1, previous, cancellationToken);
                ReadMomentRow(metal, 0, current, cancellationToken);
                ReadMomentRow(metal, 1 % height, next, cancellationToken);
            }

            using var colorHash = color is null ? null : IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var metalHash = metal is null ? null : IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            if (color is not null) colorHash!.AppendData(color.Header);
            if (metal is not null) metalHash!.AppendData(metal.Header);
            if (unequal) {
                // Authenticate every original MR row again, independently of the color grid.
                // R/G/A may vary even though metallic B is a constant reconstruction function.
                for (var y = 0; y < metal!.Height; y++) {
                    ReadMomentRow(metal, y, current, cancellationToken);
                    metalHash!.AppendData(current);
                }
            }
            // Exact products of the two separable nearest/linear reconstruction functions.
            // A constant absent source needs no filtering; either kernel preserves the mean.
            var side = color is null || metal is null ? 0d
                : color.Sampler.MinFilter == WGPUFilterMode.Nearest && metal.Sampler.MinFilter == WGPUFilterMode.Nearest ? 0d
                : color.Sampler.MinFilter == WGPUFilterMode.Linear && metal.Sampler.MinFilter == WGPUFilterMode.Linear ? 1d / 6
                : 1d / 8;
            var center = 1 - 2 * side;
            double red = 0, green = 0, blue = 0, jointRed = 0, jointGreen = 0, jointBlue = 0;
            for (var y = 0; y < height; y++) {
                cancellationToken.ThrowIfCancellationRequested();
                if (color is not null) {
                    ReadMomentRow(color, y, colorRow, cancellationToken);
                    colorHash!.AppendData(colorRow);
                }
                if (metal is not null && !unequal) metalHash!.AppendData(current);
                for (var x = 0; x < width; x++) {
                    var at = x * 4;
                    var r = color is null ? 1 : linear[colorRow[at]];
                    var g = color is null ? 1 : linear[colorRow[at + 1]];
                    var b = color is null ? 1 : linear[colorRow[at + 2]];
                    var m = unequal ? metal!.ConstantMetallic!.Value / 255d : 1d;
                    if (metal is not null && !unequal) {
                        if (side == 0) m = current[at + 2] / 255d;
                        else {
                            var left = (x == 0 ? width - 1 : x - 1) * 4 + 2;
                            var right = (x + 1 == width ? 0 : x + 1) * 4 + 2;
                            var here = at + 2;
                            var above = side * previous[left] + center * previous[here] + side * previous[right];
                            var middle = side * current[left] + center * current[here] + side * current[right];
                            var below = side * next[left] + center * next[here] + side * next[right];
                            m = System.Math.Min(1d, (side * above + center * middle + side * below) / 255d);
                        }
                    }
                    red += r; green += g; blue += b;
                    jointRed += r * m; jointGreen += g * m; jointBlue += b * m;
                }
                if (metal is not null && !unequal && y + 1 < height) {
                    var reuse = previous; previous = current; current = next; next = reuse;
                    ReadMomentRow(metal, (y + 2) % height, next, cancellationToken);
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            var count = (double)width * height;
            var result = new PbrDiffuseMoments(
                new((float)(red / count), (float)(green / count), (float)(blue / count)),
                new((float)(jointRed / count), (float)(jointGreen / count), (float)(jointBlue / count)),
                FinishMomentHash(colorHash), FinishMomentHash(metalHash));
            if (color is not null) color.Input.Position = checked(color.Origin + (long)color.EncodedBytes);
            if (metal is not null) metal.Input.Position = checked(metal.Origin + (long)metal.EncodedBytes);
            return result;
        }
        catch (EndOfStreamException error) { throw new InvalidDataException("Truncated diffuse moment texture.", error); }
    }

    private sealed class MomentSource(Stream input, long origin, byte[] header, int width, int height,
        PbrTextureSampler sampler, ulong payloadBytes)
    {
        internal readonly Stream Input = input;
        internal readonly long Origin = origin;
        internal readonly byte[] Header = header;
        internal readonly int Width = width, Height = height;
        internal readonly PbrTextureSampler Sampler = sampler;
        internal readonly ulong PayloadBytes = payloadBytes;
        internal ulong EncodedBytes => 64 + PayloadBytes;
        internal byte[] RowHashes = [];
        internal byte? ConstantMetallic;
    }

    private static MomentSource? OpenMomentSource(Stream? input, bool srgb, ulong maximumBytes,
        ref ulong admitted, CancellationToken token)
    {
        if (input is null) return null;
        if (!input.CanRead || !input.CanSeek) throw new ArgumentException("Diffuse moments require readable seekable borrowed streams.");
        token.ThrowIfCancellationRequested();
        var origin = input.Position;
        Span<byte> header = stackalloc byte[64]; input.ReadExactly(header);
        var (width, height, _, sampler) = PbrTextureData.ReadHeader(header, maximumBytes);
        if ((header[16] != 0) != srgb) throw new InvalidDataException("Diffuse moment texture has the wrong color space for its role.");
        if (sampler.AddressU != WGPUAddressMode.Repeat || sampler.AddressV != WGPUAddressMode.Repeat
            || sampler.MinFilter != sampler.MagFilter)
            throw new InvalidDataException("Diffuse moments require repeat addressing and equal minification/magnification filters.");
        var payload = BinaryPrimitives.ReadUInt64LittleEndian(header[24..]);
        AdmitMomentBytes(64 + payload + 64, maximumBytes, ref admitted);
        return new(input, origin, header.ToArray(), (int)width, (int)height, sampler, payload);
    }

    private static void ScanMomentSource(MomentSource source, byte[] scratch, CancellationToken token,
        bool findConstantMetallic)
    {
        using var complete = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var finest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        complete.AppendData(source.Header.AsSpan(0, 32));
        Span<byte> canonical = stackalloc byte[64]; source.Header.CopyTo(canonical);
        canonical[17] = 1;
        var finestBytes = (ulong)source.Width * (ulong)source.Height * 4;
        BinaryPrimitives.WriteUInt64LittleEndian(canonical[24..], finestBytes);
        finest.AppendData(canonical[..32]);
        source.Input.Position = checked(source.Origin + 64);
        var rowBytes = source.Width * 4;
        byte metallic = 0;
        var constant = true;
        for (var y = 0; y < source.Height; y++) {
            token.ThrowIfCancellationRequested();
            var row = scratch.AsSpan(0, rowBytes); source.Input.ReadExactly(row);
            complete.AppendData(row); finest.AppendData(row);
            SHA256.HashData(row, source.RowHashes.AsSpan(y * 32, 32));
            if (findConstantMetallic && constant) {
                if (y == 0) metallic = row[2];
                for (var at = 2; at < row.Length; at += 4)
                    if (row[at] != metallic) { constant = false; break; }
            }
        }
        var remaining = source.PayloadBytes - finestBytes;
        while (remaining != 0) {
            token.ThrowIfCancellationRequested();
            var block = scratch.AsSpan(0, (int)System.Math.Min((ulong)scratch.Length, remaining));
            source.Input.ReadExactly(block); complete.AppendData(block); remaining -= (ulong)block.Length;
        }
        token.ThrowIfCancellationRequested();
        Span<byte> digest = stackalloc byte[32]; complete.GetHashAndReset(digest);
        if (!CryptographicOperations.FixedTimeEquals(digest, source.Header.AsSpan(32, 32)))
            throw new InvalidDataException("Diffuse moment texture checksum mismatch.");
        if (source.Input.ReadByte() != -1) throw new InvalidDataException("Trailing diffuse moment texture data.");
        if (findConstantMetallic && constant) source.ConstantMetallic = metallic;
        finest.GetHashAndReset(canonical[32..]);
        canonical.CopyTo(source.Header);
    }

    private static void AuthenticateMomentSource(MomentSource? source, byte[] scratch, ulong maximumBytes,
        ref ulong admitted, CancellationToken token, bool findConstantMetallic = false)
    {
        if (source is null) return;
        AdmitMomentBytes((ulong)source.Height * 32, maximumBytes, ref admitted);
        source.RowHashes = new byte[source.Height * 32];
        ScanMomentSource(source, scratch, token, findConstantMetallic);
    }

    private static string? FinishMomentHash(IncrementalHash? hash)
    {
        if (hash is null) return null;
        Span<byte> digest = stackalloc byte[32]; hash.GetHashAndReset(digest);
        return Convert.ToHexString(digest);
    }

    private static void ReadMomentRow(MomentSource source, int row, byte[] bytes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        source.Input.Position = checked(source.Origin + 64 + (long)row * source.Width * 4);
        source.Input.ReadExactly(bytes);
        Span<byte> digest = stackalloc byte[32]; SHA256.HashData(bytes, digest);
        if (!CryptographicOperations.FixedTimeEquals(digest, source.RowHashes.AsSpan(row * 32, 32)))
            throw new InvalidDataException("Diffuse moment source changed after authentication.");
    }

    private static void AdmitMomentBytes(ulong bytes, ulong maximumBytes, ref ulong admitted)
    {
        if (bytes > maximumBytes - admitted) throw new InvalidDataException("Diffuse moment inputs and working arrays exceed admission.");
        admitted += bytes;
    }
}
