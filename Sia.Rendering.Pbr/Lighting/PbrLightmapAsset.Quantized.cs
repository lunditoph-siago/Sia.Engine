using System.Security.Cryptography;
using Sia.Math;

namespace Sia.Engine.Rendering.Pbr;

public sealed partial class PbrLightmapAsset
{
    internal static PbrLightmapAsset FromUnorm8(int resolution, ReadOnlySpan<PbrLightmapReceiver> receivers,
        ReadOnlySpan<byte> scene, ReadOnlySpan<byte> surface, PbrLightmapBakeSettings settings, byte[] data, float4[] scales,
        ReadOnlySpan<PbrLightmapChart> charts = default)
        => new(resolution, receivers, scene, surface, settings, data, scales, charts);

    private PbrLightmapAsset(int resolution, ReadOnlySpan<PbrLightmapReceiver> receivers,
        ReadOnlySpan<byte> scene, ReadOnlySpan<byte> surface, PbrLightmapBakeSettings settings, byte[] data, float4[] scales,
        ReadOnlySpan<PbrLightmapChart> charts)
        : this(resolution, receivers, scene, surface, settings, data.Length / 4, charts)
    {
        if (data.Length % 16 != 0 || scales.Length != receivers.Length * 4)
            throw new ArgumentException("Invalid quantized lightmap lengths.");
        ValidateDecodeScales(scales);
        ValidateUnorm8Data(data);
        Encoding = PbrLightmapEncoding.L1Unorm8;
        QuantizedData = data;
        DecodeScales = scales;
        BakeIdentity = QuantizedBakeIdentity(BakeIdentity.Span, scales);
    }

    internal static void ValidateDecodeScales(ReadOnlySpan<float4> scales)
    {
        foreach (var scale in scales)
            if (!math.all(math.isfinite(scale)) || math.any(scale.xyz < 0) || math.any(scale.xyz > new float3(65504)) || scale.w != 0)
                throw new ArgumentException("Invalid quantized lightmap scales.");
    }

    internal static void ValidateUnorm8Data(ReadOnlySpan<byte> data)
    {
        if (data.Length % 16 != 0) throw new ArgumentException("Invalid quantized pixel length.");
        for (var pixel = 0; pixel < data.Length / 16; pixel++) {
            var at = pixel * 16;
            if (data[at + 3] is not (0 or 255) || data[at + 7] != 0 || data[at + 11] != 0 || data[at + 15] != 0)
                throw new ArgumentException("Invalid quantized lightmap coverage or reserved channels.");
            if (data[at + 3] == 0) {
                for (var c = 0; c < 16; c++) if (data[at + c] != 0)
                    throw new ArgumentException("Uncovered quantized lightmap texels must be zero.");
            } else {
                for (var band = 1; band < 4; band++)
                    for (var c = 0; c < 3; c++) if (data[at + band * 4 + c] == 0)
                        throw new ArgumentException("Signed quantized coefficients reserve zero for uncovered texels.");
            }
        }
    }

    internal static byte[] QuantizedBakeIdentity(ReadOnlySpan<byte> identity, ReadOnlySpan<float4> scales)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("SIALM-L1-UNORM8-1"u8);
        hash.AppendData(identity);
        Span<byte> bytes = stackalloc byte[12];
        foreach (var scale in scales) {
            System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(bytes, scale.x);
            System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(bytes[4..], scale.y);
            System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(bytes[8..], scale.z);
            hash.AppendData(bytes);
        }
        return hash.GetHashAndReset();
    }

    /// <summary>Quantizes each receiver independently, retaining RGB L1 and valid black texels.</summary>
    public PbrLightmapAsset Quantize(int maximumBytes = 32 * 1024 * 1024, CancellationToken cancellationToken = default)
    {
        var encodedBytes = (Charts.IsEmpty ? 248L : 256L + Charts.Length * 20L)
            + Receivers.Length * 80L + (long)Resolution * Resolution * 16;
        if (maximumBytes < 0 || encodedBytes > maximumBytes)
            throw new InvalidOperationException("Quantized lightmaps exceed the encoded-byte budget.");
        cancellationToken.ThrowIfCancellationRequested();
        if (Encoding == PbrLightmapEncoding.L1Unorm8) return this;
        var data = new byte[Resolution * Resolution * 16];
        var scales = new float4[Receivers.Length * 4];
        for (var i = 0; i < Receivers.Length; i++) {
            var receiver = Receivers.Span[i];
            float4 Read(int pixel, int band) {
                var x = receiver.X + pixel % receiver.Resolution;
                var y = receiver.Y + pixel / receiver.Resolution;
                var at = (y * Resolution + x) * 16 + band * 4;
                var values = Data.Span;
                return new((float)values[at], (float)values[at + 1], (float)values[at + 2], (float)values[at + 3]);
            }
            QuantizeReceiver(receiver, Resolution, data, scales.AsSpan(i * 4, 4), Read, cancellationToken);
        }
        return FromUnorm8(Resolution, Receivers.Span, SceneIdentity.Span, SurfaceIdentity.Span, Settings, data, scales, Charts.Span);
    }

    internal static void QuantizeReceiver(PbrLightmapReceiver receiver, int atlasResolution, byte[] destination,
        Span<float4> scales, Func<int, int, float4> read, CancellationToken cancellationToken)
    {
        for (var pixel = 0; pixel < receiver.Resolution * receiver.Resolution; pixel++) {
            if ((pixel & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (read(pixel, 0).w == 0) continue;
            for (var band = 0; band < 4; band++) {
                var value = read(pixel, band);
                ValidateCoefficient(value, band);
                scales[band] = new(math.max(scales[band].xyz, math.abs(value.xyz)), 0);
            }
        }
        for (var y = 0; y < receiver.Resolution; y++) {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < receiver.Resolution; x++) {
                var pixel = y * receiver.Resolution + x;
                if (read(pixel, 0).w == 0) continue;
                var target = ((receiver.Y + y) * atlasResolution + receiver.X + x) * 16;
                destination[target + 3] = 255;
                for (var band = 0; band < 4; band++) {
                    var value = read(pixel, band);
                    var scale = scales[band];
                    destination[target + band * 4] = QuantizedChannel(value.x, scale.x, band);
                    destination[target + band * 4 + 1] = QuantizedChannel(value.y, scale.y, band);
                    destination[target + band * 4 + 2] = QuantizedChannel(value.z, scale.z, band);
                }
            }
        }
    }

    private static byte QuantizedChannel(float value, float scale, int band)
    {
        var normalized = scale == 0 ? 0 : value / scale;
        return (byte)System.Math.Clamp((int)MathF.Round(normalized * (band == 0 ? 255 : 127) + (band == 0 ? 0 : 128)),
            band == 0 ? 0 : 1, 255);
    }

    private float3 QuantizedIrradiance(int x, int y, float3 normal)
    {
        var at = (y * Resolution + x) * 16;
        if (QuantizedData.Span[at + 3] == 0) return default;
        for (var i = 0; i < Receivers.Length; i++) {
            var r = Receivers.Span[i];
            if (x < r.X || x >= r.X + r.Resolution || y < r.Y || y >= r.Y + r.Resolution) continue;
            float3 Coefficient(int band) {
                var values = QuantizedData.Span;
                var offset = at + band * 4;
                var v = new float3(values[offset], values[offset + 1], values[offset + 2]);
                return band == 0 ? v * DecodeScales.Span[i * 4].xyz / 255
                    : (v - new float3(128)) * DecodeScales.Span[i * 4 + band].xyz / 127;
            }
            return math.max(Coefficient(0) * (.2820948f * MathF.PI)
                + (Coefficient(1) * normal.y + Coefficient(2) * normal.z + Coefficient(3) * normal.x)
                    * (.4886025f * (2 * MathF.PI / 3)), float3.zero);
        }
        return default;
    }
}
