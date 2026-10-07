using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Sia.Engine.Rendering;
using Sia.Engine.Rendering.Pbr;
using Sia.Math;
using Xunit;

namespace Sia.Rendering.Debug.Tests;

public sealed class SceneTraceAssetTests
{
    private static SceneTraceData Trace(int count = 1)
        => new(Enumerable.Range(0, count).Select(i => new SceneTraceTriangle(
            new(i * 2, 0, 0), new(i * 2 + 1, 0, 0), new(i * 2, 1, 0),
            new(1, .5f, .25f), new(.1f, .2f, .3f), true)).ToArray(),
            sceneIdentity: Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());

    private static byte[] Encode(SceneTraceData value)
    {
        using var output = new MemoryStream(); value.Write(output);
        Assert.True(output.CanWrite);
        return output.ToArray();
    }

    [Fact]
    public void SingleTriangleHasAnIndependentExactLittleEndianEncoding()
    {
        using var body = new MemoryStream();
        using (var w = new BinaryWriter(body, System.Text.Encoding.UTF8, leaveOpen: true)) {
            w.Write("SIATRACE"u8); w.Write(1); w.Write(1); w.Write(11);
            w.Write(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());
            float[] records = [1, 5, 1, 9, 6, 0, 0, 0,
                0, 0, 0, 1, 1, 1, 0, 0, 1, 0, 0, 0,
                0, 1, 2, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0,
                1, .5f, .25f, 1, .1f, .2f, .3f, 0];
            foreach (var value in records) w.Write(value);
        }
        var payload = body.ToArray();
        byte[] expected = [.. payload, .. SHA256.HashData(payload)];
        Assert.Equal(expected, Encode(Trace()));
        Assert.Equal(Trace().Packed.ToArray(), SceneTraceData.Decode(expected).Packed.ToArray());
    }

    [Theory]
    [InlineData(1)] [InlineData(8)] [InlineData(9)] [InlineData(17)] [InlineData(513)]
    public void StreamAndBufferReadersPreserveOwnedDataAcrossHierarchyShapes(int count)
    {
        var original = Trace(count); var encoded = Encode(original);
        using var source = new OneByteStream(encoded);
        var streamed = SceneTraceData.Read(source);
        var buffered = SceneTraceData.Decode(encoded);
        Array.Clear(encoded);
        foreach (var actual in new[] { streamed, buffered }) {
            Assert.Equal(count, actual.TriangleCount);
            Assert.Equal(original.Bounds, actual.Bounds);
            Assert.Equal(original.Identity.ToArray(), actual.Identity.ToArray());
            Assert.Equal(MemoryMarshal.AsBytes(original.Packed.Span).ToArray(), MemoryMarshal.AsBytes(actual.Packed.Span).ToArray());
        }
        Assert.True(source.CanRead);
    }

    [Fact]
    public void SizeAdmissionRejectsBeforeReadingTheBody()
    {
        var encoded = Encode(Trace());
        using var prefix = new MemoryStream(encoded[..52]);
        Assert.Throws<InvalidDataException>(() => SceneTraceData.Read(prefix, maximumBytes: 1));
        Assert.Equal(52, prefix.Position);
        Assert.Throws<InvalidDataException>(() => SceneTraceData.Decode(encoded, maximumBytes: 1));
    }

    [Theory]
    [InlineData(8, 2)] [InlineData(12, 0)] [InlineData(12, 4_000_001)] [InlineData(16, int.MaxValue)]
    public void InvalidPrefixSizesAndVersionFailBeforeBodyAllocation(int offset, int value)
    {
        var header = Encode(Trace())[..52];
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(offset), value);
        using var source = new MemoryStream(header);
        Assert.Throws<InvalidDataException>(() => SceneTraceData.Read(source));
    }

    [Theory]
    [InlineData("checksum")] [InlineData("trailing")] [InlineData("truncated")]
    public void CorruptIncompleteOrExtendedPayloadIsRejected(string kind)
    {
        var bytes = Encode(Trace());
        if (kind == "checksum") bytes[30] ^= 1;
        else if (kind == "trailing") bytes = [.. bytes, 0];
        else bytes = bytes[..^1];
        Assert.Throws<InvalidDataException>(() => SceneTraceData.Decode(bytes));
        using var source = new MemoryStream(bytes);
        if (kind == "truncated") Assert.Throws<EndOfStreamException>(() => SceneTraceData.Read(source));
        else Assert.Throws<InvalidDataException>(() => SceneTraceData.Read(source));
    }

    [Theory]
    [InlineData(8, float.NaN)] // Root minimum.
    [InlineData(11, 0)] // Root escape index.
    [InlineData(15, 1)] // Root first triangle.
    [InlineData(16, 9)] // Leaf count above eight.
    [InlineData(20, -1)] // Triangle vertex index.
    [InlineData(20, .5f)]
    [InlineData(20, float.NaN)]
    [InlineData(20, float.PositiveInfinity)]
    [InlineData(23, 1)] // Triangle surface index.
    [InlineData(27, 1)] // Vertex padding.
    [InlineData(24, float.PositiveInfinity)]
    [InlineData(36, -1)] // Negative albedo.
    [InlineData(39, .5f)] // Double-sided flag.
    [InlineData(40, -1)] // Negative emission.
    [InlineData(43, 1)] // Emission padding.
    public void RechecksummedInvalidGpuDataIsRejected(int word, float value)
    {
        var bytes = Encode(Trace());
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(52 + word * 4), value);
        SHA256.HashData(bytes.AsSpan(0, bytes.Length - 32), bytes.AsSpan(bytes.Length - 32));
        Assert.Throws<InvalidDataException>(() => SceneTraceData.Decode(bytes));
        using var input = new MemoryStream(bytes);
        Assert.Throws<InvalidDataException>(() => SceneTraceData.Read(input));
    }

    [Fact]
    public void RechecksummedInternalNodeCannotReassignItsLeafRange()
    {
        var bytes = Encode(Trace(17));
        // The left child contains the first eight triangles; it cannot start at one.
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(52 + (2 + 3 + 1) * 16 + 12), 1);
        SHA256.HashData(bytes.AsSpan(0, bytes.Length - 32), bytes.AsSpan(bytes.Length - 32));
        Assert.Throws<InvalidDataException>(() => SceneTraceData.Decode(bytes));
    }

    [Fact]
    public void PreparedInputRequiresItsCanonicalIdentityAndRemainingBudget()
    {
        var trace = Trace();
        var settings = PbrRendererSettings.ForQuality(RenderQuality.High) with { StaticTransport = trace };
        Assert.Same(trace, settings.GetStaticTransport(trace.Identity.Span, (ulong)trace.Packed.Length * 16));
        Assert.Throws<ArgumentException>(() => settings.GetStaticTransport(new byte[32], ulong.MaxValue));
        Assert.Throws<ArgumentException>(() => settings.GetStaticTransport(trace.Identity.Span, 1));
        Assert.Null((settings with { DynamicSceneGi = false }).GetStaticTransport([], 0));
    }

    private sealed class OneByteStream(byte[] bytes) : Stream
    {
        private int _offset;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            if (buffer.IsEmpty || _offset == bytes.Length) return 0;
            buffer[0] = bytes[_offset++]; return 1;
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
