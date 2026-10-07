using System.Buffers.Binary;
using System.Security.Cryptography;
using Sia.Engine.Mesh;
using Sia.Engine.Rendering.Pbr;
using Sia.Math;
using Xunit;

namespace Sia.Rendering.Debug.Tests;

public sealed class LightmapQuantizationTests
{
    private static PbrLightmapAsset Reference()
    {
        PbrLightmapReceiver[] receivers = [new(-1, 0, 0, 0, 8, default), new(-1, 2, 8, 0, 8, default), new(-1, 7, 0, 8, 8, default)];
        var coefficients = new float4[16 * 16 * 4];
        for (var i = 0; i < receivers.Length; i++) {
            var r = receivers[i];
            var intensity = new[] { .01f, 1f, 2000f }[i];
            for (var y = 0; y < 8; y++)
                for (var x = 0; x < 8; x++) {
                    if (x == 0 && y == 0) continue; // An actual coverage hole.
                    var at = ((r.Y + y) * 16 + r.X + x) * 4;
                    coefficients[at] = new(default(float3), 1);
                    if (x == 1 && y == 0) continue; // Valid black beside the hole.
                    var color = new float3(1 + x / 7f, .2f + y / 7f, .05f + (x + y) / 14f) * intensity;
                    coefficients[at] = new(color, 1);
                    coefficients[at + 1] = new(color * new float3(.8f, -.4f, .1f), 0);
                    coefficients[at + 2] = new(color * new float3(-.2f, .6f, -.7f), 0);
                    coefficients[at + 3] = new(color * new float3(.3f, -.1f, .5f), 0);
                }
        }
        return new(16, receivers, new byte[32], Enumerable.Repeat((byte)1, 32).ToArray(), new(), coefficients);
    }

    [Fact]
    public void PerReceiverRgbL1QuantizationHasBoundedErrorAndPreservesHdrCoverageAndBlack()
    {
        var reference = Reference();
        var original = reference.Encode();
        var quantized = reference.Quantize();
        Assert.Equal(PbrLightmapEncoding.L1Unorm8, quantized.Encoding);
        Assert.Equal(16, quantized.BytesPerTexel);
        Assert.Equal(reference.TextureBytes / 2, quantized.TextureBytes);
        Assert.Empty(quantized.Data.ToArray());
        Assert.Equal(248 + 80 * 3 + 16 * 16 * 16, quantized.Encode().Length);
        Assert.Equal(original, reference.Encode());
        Assert.True(reference.SceneIdentity.Span.SequenceEqual(quantized.SceneIdentity.Span));
        Assert.False(reference.BakeIdentity.Span.SequenceEqual(quantized.BakeIdentity.Span));
        for (var receiver = 0; receiver < 3; receiver++) {
            var r = quantized.Receivers.Span[receiver];
            Assert.Equal(float3.zero, quantized.Irradiance(r.X, r.Y, new(0, 0, 1)));
            Assert.Equal(float3.zero, quantized.Irradiance(r.X + 1, r.Y, new(0, 0, 1)));
            Assert.Equal(0, quantized.QuantizedData.Span[(r.Y * 16 + r.X) * 16 + 3]);
            Assert.Equal(255, quantized.QuantizedData.Span[(r.Y * 16 + r.X + 1) * 16 + 3]);
            var scales = quantized.DecodeScales.Span.Slice(receiver * 4, 4);
            for (var sample = 0; sample < 64; sample++) {
                var n = math.normalize(new float3(MathF.Sin(sample * 1.31f), MathF.Cos(sample * .71f), MathF.Sin(sample * .37f + 1)));
                var bound = scales[0].xyz * (.2820948f * MathF.PI / 510)
                    + (scales[1].xyz * MathF.Abs(n.y) + scales[2].xyz * MathF.Abs(n.z) + scales[3].xyz * MathF.Abs(n.x))
                        * (.4886025f * (2 * MathF.PI / 3) / 254);
                var error = math.abs(reference.Irradiance(r.X + sample % 8, r.Y + sample / 8, n)
                    - quantized.Irradiance(r.X + sample % 8, r.Y + sample / 8, n));
                Assert.True(math.all(error <= bound + scales[0].xyz * 2e-6f + new float3(1e-6f)));
            }
        }
        Assert.Equal(float3.zero, quantized.Irradiance(15, 15, new(0, 0, 1)));
        Assert.Equal(quantized.Encode(), PbrLightmapAsset.Decode(quantized.Encode()).Encode());
        Assert.Same(quantized, quantized.Quantize());
        Assert.Throws<OperationCanceledException>(() => quantized.Quantize(cancellationToken: new(true)));
        Assert.Throws<InvalidOperationException>(() => reference.Quantize(quantized.Encode().Length - 1));
        Assert.Equal(quantized.Encode(), reference.Quantize(quantized.Encode().Length).Encode());
    }

    [Fact]
    public void CompactAdaptiveBakeWritesDirectlyAndFitsItsActualEncodedBudget()
    {
        var mesh = MeshPatchAsset.Cook(new([
            new(new(0, 0, 0), new(0, 0, 1), default), new(new(1, 0, 0), new(0, 0, 1), default),
            new(new(1, 1, 0), new(0, 0, 1), default), new(new(0, 1, 0), new(0, 0, 1), default)],
            [0, 1, 2, 0, 2, 3], new(float3.zero, new(1, 1, 0))));
        var scene = PbrSceneAsset.Create([mesh], [new(new() { BaseColor = new(1) }, DoubleSided: true)], [new(0, 0, float4x4.identity)]);
        var options = new PbrLightmapBakeSettings { Sky = new() { Horizon = new(2), Zenith = new(2), Ground = new(2), SunRadiance = default }, Samples = 64 };
        var half = PbrLightmapBaker.BakeAdaptive(scene, out var mapped, options, 16, 16);
        var expected = half.Quantize();
        var compact = PbrLightmapBaker.BakeAdaptive(scene, out var compactScene, options, 16, 16,
            maximumAssetBytes: (ulong)expected.Encode().Length, encoding: PbrLightmapEncoding.L1Unorm8);
        Assert.Equal(expected.Encode(), compact.Encode());
        Assert.Equal(mapped.Encode(), compactScene.Encode());
        Assert.Throws<InvalidOperationException>(() => PbrLightmapBaker.BakeAdaptive(scene, out _, options, 16, 16,
            maximumAssetBytes: (ulong)expected.Encode().Length)); // Half cannot fit that same budget.
        Assert.Throws<InvalidOperationException>(() => PbrLightmapBaker.BakeAdaptive(scene, out _, options, 16, 16,
            maximumAssetBytes: (ulong)expected.Encode().Length - 1, encoding: PbrLightmapEncoding.L1Unorm8));
        Assert.Throws<ArgumentOutOfRangeException>(() => PbrLightmapBaker.BakeAdaptive(scene, out _, encoding: (PbrLightmapEncoding)99));
    }

    [Fact]
    public void VersionedCompactCodecRejectsInvalidScalesCoverageLengthsAndManifest()
    {
        var original = Reference().Quantize().Encode();
        byte[] Mutate(Action<byte[]> change) {
            var bytes = original.ToArray(); change(bytes);
            SHA256.HashData(bytes.AsSpan(0, bytes.Length - 32)).CopyTo(bytes.AsSpan(bytes.Length - 32));
            return bytes;
        }
        Assert.Throws<InvalidDataException>(() => PbrLightmapAsset.Decode(original, original.Length - 1));
        Assert.Throws<InvalidDataException>(() => PbrLightmapAsset.Decode(Mutate(b => BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(232), float.NaN))));
        Assert.Throws<InvalidDataException>(() => PbrLightmapAsset.Decode(Mutate(b => BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(232), -1))));
        Assert.Throws<InvalidDataException>(() => PbrLightmapAsset.Decode(Mutate(b => b[216 + 80 * 3 + 3] = 254)));
        Assert.Throws<InvalidDataException>(() => PbrLightmapAsset.Decode(Mutate(b => b[216 + 80 * 3 + 16 + 4] = 0)));
        Assert.Throws<InvalidDataException>(() => PbrLightmapAsset.Decode(Mutate(b => b[216 + 80 * 3 + 7] = 1)));
        Assert.Throws<InvalidDataException>(() => PbrLightmapAsset.Decode(Mutate(b => b[80] ^= 1)));
        Assert.Throws<InvalidDataException>(() => PbrLightmapAsset.Decode(original.AsSpan(0, original.Length - 1)));
    }
}
