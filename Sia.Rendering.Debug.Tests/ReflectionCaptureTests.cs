using System.Security.Cryptography;
using Sia.Engine.Rendering;
using Sia.Engine.Rendering.Pbr;
using Sia.Math;
using Xunit;

namespace Sia.Rendering.Debug.Tests;

public sealed class ReflectionCaptureTests
{
    internal static IblEnvironmentAsset Environment()
    {
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, System.Text.Encoding.UTF8, leaveOpen: true)) {
            writer.Write("SIAENV\0\0"u8);
            foreach (var v in new[] { float3.zero, float3.zero, float3.zero, new float3(0, 1, 0), float3.zero }) {
                writer.Write(v.x); writer.Write(v.y); writer.Write(v.z);
            }
            writer.Write(256f); writer.Write(1f);
            writer.Write(new byte[9 * 16 + (IblEnvironmentAsset.CubeTexels + IblEnvironmentAsset.LutTexels) * 8]);
        }
        var data = bytes.ToArray(); bytes.Write(SHA256.HashData(data)); bytes.Position = 0;
        return IblEnvironmentAsset.Read(bytes);
    }

    [Fact]
    public void ConstantCaptureSurvivesEveryMipAndRetainsDiffuseAndBrdf()
    {
        var source = Environment();
        var calls = 0;
        var baked = IblEnvironmentBaker.BakeRadiance(source, (direction, background) => {
            Interlocked.Increment(ref calls);
            Assert.InRange(math.length(direction), .99999f, 1.00001f);
            return new(2, 3, 4);
        });
        Assert.Equal(6 * 128 * 128, calls); // Filtering never repeats scene tracing.
        for (var i = 0; i < baked.Cube.Length; i += 4) {
            Assert.Equal((Half)2, baked.Cube.Span[i]); Assert.Equal((Half)3, baked.Cube.Span[i + 1]);
            Assert.Equal((Half)4, baked.Cube.Span[i + 2]); Assert.Equal((Half)1, baked.Cube.Span[i + 3]);
        }
        Assert.True(source.Coefficients.Span.SequenceEqual(baked.Coefficients.Span));
        Assert.True(source.BrdfLut.Span.SequenceEqual(baked.BrdfLut.Span));
    }

    [Fact]
    public void CaptureCodecIsBoundedAndRejectsCorruptionUnknownVersionAndTrailingData()
    {
        var source = Environment();
        var capture = new PbrReflectionCaptureAsset(new byte[32], PbrReflectionCaptureAsset.HashEnvironment(source),
            new(0, 0, 0), new(new(-1), new(1)), source);
        using var bytes = new MemoryStream(); capture.Write(bytes);
        Assert.Equal(PbrReflectionCaptureAsset.EncodedBytes, bytes.Length);
        bytes.Position = 0; var restored = PbrReflectionCaptureAsset.Read(bytes);
        using var roundtrip = new MemoryStream(); restored.Write(roundtrip);
        Assert.Equal(bytes.ToArray(), roundtrip.ToArray());
        var corrupt = bytes.ToArray(); corrupt[64] ^= 1;
        Assert.Throws<InvalidDataException>(() => PbrReflectionCaptureAsset.Read(new MemoryStream(corrupt)));
        var version = bytes.ToArray(); version[7] = (byte)'9';
        SHA256.HashData(version.AsSpan(0, version.Length - 32)).CopyTo(version.AsSpan(version.Length - 32));
        Assert.Throws<InvalidDataException>(() => PbrReflectionCaptureAsset.Read(new MemoryStream(version)));
        Assert.Throws<InvalidDataException>(() => PbrReflectionCaptureAsset.Read(new MemoryStream([.. bytes.ToArray(), 0])));
        Assert.Throws<EndOfStreamException>(() => PbrReflectionCaptureAsset.Read(new MemoryStream(bytes.ToArray()[..100])));
    }

    [Fact]
    public void InvalidBoxAndCancelledBakeHaveNoRadianceEffects()
    {
        var source = Environment();
        Assert.Throws<ArgumentException>(() => new PbrReflectionCaptureAsset(new byte[32], new byte[32],
            new(1), new(new(-1), new(1)), source));
        Assert.Throws<ArgumentException>(() => new PbrReflectionCaptureAsset(new byte[32], new byte[32],
            new(float.NaN), new(new(-1), new(1)), source));
        var calls = 0;
        Assert.ThrowsAny<OperationCanceledException>(() => IblEnvironmentBaker.BakeRadiance(source,
            (_, _) => { calls++; return float3.zero; }, new CancellationToken(true)));
        Assert.Equal(0, calls);
    }
}
