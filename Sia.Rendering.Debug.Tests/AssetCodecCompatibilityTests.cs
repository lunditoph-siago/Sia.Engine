using System.Buffers.Binary;
using System.Security.Cryptography;
using Sia.Engine.Rendering;
using Sia.Engine.Rendering.Pbr;
using Xunit;

namespace Sia.Rendering.Debug.Tests;

public sealed class AssetCodecCompatibilityTests
{
    // Independent encoders of the existing wire formats: do not call production Write.
    private static byte[] Envelope(Action<BinaryWriter> encode)
    {
        using var body = new MemoryStream();
        using (var writer = new BinaryWriter(body, System.Text.Encoding.UTF8, true)) encode(writer);
        var bytes = body.ToArray();
        return [.. bytes, .. SHA256.HashData(bytes)];
    }

    private static byte[] LegacyEnvironment() => Envelope(w => {
        w.Write("SIAENV\0\0"u8);
        foreach (var v in new float[] { .1f, .2f, .3f, .4f, .5f, .6f, .05f, .06f, .07f,
            0, 1, 0, 4, 3, 2, 256, 1.5f }) w.Write(v);
        for (var i = 0; i < 9; i++) {
            w.Write(i * -.125f); w.Write(i * .25f); w.Write(i * .5f); w.Write(1f);
        }
        for (var i = 0; i < (IblEnvironmentAsset.CubeTexels + IblEnvironmentAsset.LutTexels) * 4; i++)
            w.Write(BitConverter.HalfToUInt16Bits((Half)((i % 37) * .125f)));
    });

    private static byte[] LegacyProbes() => Envelope(w => {
        w.Write("SIAPROBE"u8);
        w.Write(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());
        foreach (var v in new float[] { -3, 2, 5, .5f, 1, 2 }) w.Write(v);
        w.Write(2u); w.Write(2u); w.Write(2u);
        for (var i = 0; i < 8 * 9; i++) {
            w.Write(i * -.125f); w.Write(i * .25f); w.Write(i * .5f); w.Write((i % 4) / 3f);
        }
    });

    private static byte[] LegacyCapture() => Envelope(w => {
        var environment = LegacyEnvironment();
        w.Write("SIAREFL1"u8);
        w.Write(Enumerable.Range(0, 32).Select(i => (byte)(255 - i)).ToArray());
        w.Write(SHA256.HashData(environment));
        foreach (var v in new float[] { -.2f, .1f, .5f, -2, -2, -2, 3, 3, 3 }) w.Write(v);
        w.Write(environment);
    });

    private static byte[] Fixture(string kind) => kind switch {
        "environment" => LegacyEnvironment(), "probes" => LegacyProbes(), "capture" => LegacyCapture(),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static void Roundtrip(string kind, Stream source, Stream destination)
    {
        switch (kind) {
            case "environment": IblEnvironmentAsset.Read(source).Write(destination); break;
            case "probes": DiffuseProbeAsset.Read(source).Write(destination); break;
            case "capture": PbrReflectionCaptureAsset.Read(source).Write(destination); break;
            default: throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    [Theory]
    [InlineData("environment")]
    [InlineData("probes")]
    [InlineData("capture")]
    public void LegacyBytesSurviveShortNonSeekableReadsAndBorrowedStreamsStayOpen(string kind)
    {
        var expected = Fixture(kind);
        using var source = new ShortReadStream(expected);
        using var destination = new MemoryStream();
        Roundtrip(kind, source, destination);
        Assert.Equal(expected, destination.ToArray());
        Assert.False(source.WasDisposed);
        Assert.True(destination.CanWrite);
        Assert.Equal(expected.Length, source.BytesRead);
    }

    [Theory]
    [InlineData("environment")]
    [InlineData("probes")]
    [InlineData("capture")]
    public void CorruptionUnknownFormatTrailingBytesAndTruncationAreRejected(string kind)
    {
        var original = Fixture(kind);
        var corrupt = original.ToArray(); corrupt[corrupt.Length / 2] ^= 1;
        var unknown = original.ToArray(); unknown[0] ^= 1;
        SHA256.HashData(unknown.AsSpan(0, unknown.Length - 32)).CopyTo(unknown.AsSpan(unknown.Length - 32));
        foreach (var invalid in new[] { corrupt, unknown, [.. original, (byte)0] }) {
            using var source = new ShortReadStream(invalid);
            using var destination = new MemoryStream();
            Assert.Throws<InvalidDataException>(() => Roundtrip(kind, source, destination));
            Assert.False(source.WasDisposed);
            Assert.Empty(destination.ToArray());
        }
        foreach (var length in new[] { 7, original.Length - 1 }) {
            using var source = new ShortReadStream(original[..length]);
            using var destination = new MemoryStream();
            Assert.Throws<EndOfStreamException>(() => Roundtrip(kind, source, destination));
            Assert.False(source.WasDisposed);
        }
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(4096u)]
    [InlineData(uint.MaxValue)]
    public void InvalidProbeDimensionsFailAfterOnlyTheBoundedPrefix(uint dimension)
    {
        var bytes = LegacyProbes();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(64), dimension);
        using var source = new ShortReadStream(bytes);
        Assert.Throws<InvalidDataException>(() => DiffuseProbeAsset.Read(source));
        Assert.Equal(76, source.BytesRead);
    }

    [Fact]
    public void CaptureAlsoChecksTheNestedEnvironmentChecksum()
    {
        var bytes = LegacyCapture();
        bytes[^33] ^= 1; // Inner checksum, with a valid outer checksum.
        SHA256.HashData(bytes.AsSpan(0, bytes.Length - 32)).CopyTo(bytes.AsSpan(bytes.Length - 32));
        using var source = new ShortReadStream(bytes);
        Assert.Throws<InvalidDataException>(() => PbrReflectionCaptureAsset.Read(source));
    }

    private sealed class ShortReadStream(byte[] bytes) : Stream
    {
        private int _position;
        public int BytesRead => _position;
        public bool WasDisposed { get; private set; }
        public override bool CanRead => !WasDisposed;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            var count = System.Math.Min(7, System.Math.Min(buffer.Length, bytes.Length - _position));
            bytes.AsSpan(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { WasDisposed = true; base.Dispose(disposing); }
    }
}
