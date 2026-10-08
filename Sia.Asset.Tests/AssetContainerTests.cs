using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using Xunit;

namespace Sia.Asset.Tests;

public sealed class AssetContainerTests
{
    private sealed class Mesh;
    private sealed class Texture;

    [Fact]
    public void WritesExactLittleEndianHeaderAndIndependentDigest()
    {
        var container = AssetContainer.Encode(7, [1, 2, 3]);
        var expectedPrefix = Convert.FromHexString("5349414153534554070000010000000003000000000000000300000000000000");
        Assert.Equal(expectedPrefix, container[..32]);
        Assert.Equal(SHA256.HashData([.. expectedPrefix, 1, 2, 3]), container[32..64]);
        Assert.Equal(new byte[] { 1, 2, 3 }, container[64..]);
    }

    [Fact]
    public async Task ReadsIndependentFixtureThroughShortNonseekableReadsWithoutClosingBorrowedStream()
    {
        var fixture = Fixture([9, 8, 7], 3);
        var reference = AssetContainer.GetReference<Mesh>(fixture, 7);
        using var input = new ShortStream(fixture);
        Assert.Equal(new byte[] { 9, 8, 7 }, await AssetContainer.ReadAsync(input, reference, 7, 3, 3));
        Assert.False(input.Disposed);
        Assert.False(input.CanSeek);
        Assert.Equal(fixture.Length, input.BytesRead);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RoundTripsBoundedRawAndBrotliPayloads(bool compressed)
    {
        var payload = Enumerable.Range(0, 4096).Select(i => (byte)(i % 7)).ToArray();
        var encoding = compressed ? AssetEncoding.Brotli : AssetEncoding.Raw;
        var container = AssetContainer.Encode(7, payload, encoding);
        var reference = AssetContainer.GetReference<Mesh>(container, 7);
        using var input = new ShortStream(container);
        Assert.Equal(payload, await AssetContainer.ReadAsync(input, reference, 7, container.Length, payload.Length));
        if (compressed) Assert.True(reference.EncodedBytes < reference.DecodedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptyPayloadHasExactConsumption(bool compressed)
    {
        var container = AssetContainer.Encode(7, [], compressed ? AssetEncoding.Brotli : AssetEncoding.Raw);
        var reference = AssetContainer.GetReference<Mesh>(container, 7);
        using var input = new ShortStream(container);
        Assert.Empty(await AssetContainer.ReadAsync(input, reference, 7, container.Length, 0));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(8, 8)]
    [InlineData(10, 7)]
    [InlineData(11, 2)]
    [InlineData(12, 1)]
    [InlineData(16, 255)]
    public async Task RejectsInvalidHeaderBeforePayloadReads(int offset, byte value)
    {
        var fixture = Fixture([1, 2, 3], 3);
        var reference = AssetContainer.GetReference<Mesh>(fixture, 7);
        fixture[offset] = value;
        using var input = new ShortStream(fixture);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await AssetContainer.ReadAsync(input, reference, 7, 3, 3));
        Assert.Equal(64, input.BytesRead);
    }

    [Fact]
    public async Task RejectsUnrepresentableDeclaredLengthsBeforePayloadReads()
    {
        var fixture = Fixture([1, 2, 3], 3);
        var reference = AssetContainer.GetReference<Mesh>(fixture, 7);
        BinaryPrimitives.WriteUInt64LittleEndian(fixture.AsSpan(16), ulong.MaxValue);
        using var input = new ShortStream(fixture);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await AssetContainer.ReadAsync(input, reference, 7, 3, 3));
        Assert.Equal(64, input.BytesRead);
    }

    [Fact]
    public async Task RejectsReferenceBudgetBeforeAnyRead()
    {
        var fixture = Fixture([1, 2, 3], 3);
        var reference = AssetContainer.GetReference<Mesh>(fixture, 7);
        using var input = new ShortStream(fixture);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await AssetContainer.ReadAsync(input, reference, 7, 2, 3));
        await Assert.ThrowsAsync<InvalidDataException>(async () => await AssetContainer.ReadAsync(input, reference, 7, 3, 2));
        Assert.Equal(0, input.BytesRead);
    }

    [Fact]
    public async Task RejectsConflictingReferenceAndDomainKind()
    {
        var fixture = Fixture([1, 2, 3], 3);
        var other = AssetContainer.GetReference<Mesh>(Fixture([3, 2, 1], 3), 7);
        using var wrongId = new ShortStream(fixture);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await AssetContainer.ReadAsync(wrongId, other, 7, 3, 3));
        Assert.Equal(64, wrongId.BytesRead);
        using var wrongKind = new ShortStream(fixture);
        var texture = AssetContainer.GetReference<Texture>(fixture, 7);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await AssetContainer.ReadAsync(wrongKind, texture, 8, 3, 3));
        Assert.Equal(64, wrongKind.BytesRead);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectsCorruptionAndTrailingBytes(bool trailing)
    {
        var fixture = Fixture([1, 2, 3], 3);
        var reference = AssetContainer.GetReference<Mesh>(fixture, 7);
        if (trailing) fixture = [.. fixture, 0];
        else fixture[^1] ^= 255;
        using var input = new ShortStream(fixture);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await AssetContainer.ReadAsync(input, reference, 7, 3, 3));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(63)]
    [InlineData(64)]
    [InlineData(66)]
    public async Task RejectsTruncatedInput(int length)
    {
        var fixture = Fixture([1, 2, 3], 3);
        var reference = AssetContainer.GetReference<Mesh>(fixture, 7);
        using var input = new ShortStream(fixture[..length]);
        await Assert.ThrowsAsync<EndOfStreamException>(async () => await AssetContainer.ReadAsync(input, reference, 7, 3, 3));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public async Task RejectsRechecksummedBrotliWithWrongDecodedLength(int delta)
    {
        var payload = new byte[1024];
        var fixture = Fixture(Compress(payload), payload.Length + delta, AssetEncoding.Brotli);
        var reference = AssetContainer.GetReference<Mesh>(fixture, 7);
        using var input = new ShortStream(fixture);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await AssetContainer.ReadAsync(input, reference, 7, fixture.Length, 2048));
    }

    [Fact]
    public async Task RejectsRechecksummedConcatenatedCompressedStreams()
    {
        var compressed = Compress([1, 2, 3]);
        var fixture = Fixture([.. compressed, .. compressed], 6, AssetEncoding.Brotli);
        var reference = AssetContainer.GetReference<Mesh>(fixture, 7);
        using var input = new ShortStream(fixture);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await AssetContainer.ReadAsync(input, reference, 7, fixture.Length, 6));
    }

    [Fact]
    public async Task CancellationAndUninitializedReferenceFailBeforeReads()
    {
        var fixture = Fixture([1, 2, 3], 3);
        var reference = AssetContainer.GetReference<Mesh>(fixture, 7);
        using var input = new ShortStream(fixture);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await AssetContainer.ReadAsync(input, reference, 7, 3, 3, new CancellationToken(true)));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await AssetContainer.ReadAsync(input, default(AssetReference<Mesh>), 7, 3, 3));
        Assert.Equal(0, input.BytesRead);
    }

    [Fact]
    public void IdentityIncludesDomainAndEncodingAndOwnsItsDigest()
    {
        var first = AssetContainer.GetReference<Mesh>(AssetContainer.Encode(7, [1, 2, 3]), 7);
        var otherKind = AssetContainer.GetReference<Texture>(AssetContainer.Encode(8, [1, 2, 3]), 8);
        var compressed = AssetContainer.GetReference<Mesh>(AssetContainer.Encode(7, [1, 2, 3], AssetEncoding.Brotli), 7);
        Assert.NotEqual(first.Id, otherKind.Id);
        Assert.NotEqual(first.Id, compressed.Id);
        Assert.Equal(first.Id, AssetId.Parse(first.Id.ToString()));
        var digest = new byte[32];
        first.Id.WriteBytes(digest);
        var copied = new AssetId(digest);
        Array.Clear(digest);
        Assert.Equal(first.Id, copied);
    }

    private static byte[] Compress(byte[] payload)
    {
        using var output = new MemoryStream();
        using (var stream = new BrotliStream(output, CompressionLevel.Optimal, leaveOpen: true)) stream.Write(payload);
        return output.ToArray();
    }

    private static byte[] Fixture(byte[] encoded, int decodedBytes, AssetEncoding encoding = AssetEncoding.Raw)
    {
        var prefix = Convert.FromHexString("5349414153534554070000010000000000000000000000000000000000000000");
        prefix[10] = (byte)encoding;
        BinaryPrimitives.WriteUInt64LittleEndian(prefix.AsSpan(16), (ulong)encoded.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(prefix.AsSpan(24), (ulong)decodedBytes);
        return [.. prefix, .. SHA256.HashData([.. prefix, .. encoded]), .. encoded];
    }

    private sealed class ShortStream(byte[] bytes) : Stream
    {
        private int _position;
        public int BytesRead => _position;
        public bool Disposed { get; private set; }
        public override bool CanRead => !Disposed;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(Disposed, this);
            if (_position == bytes.Length || buffer.Length == 0) return ValueTask.FromResult(0);
            buffer.Span[0] = bytes[_position++];
            return ValueTask.FromResult(1);
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
