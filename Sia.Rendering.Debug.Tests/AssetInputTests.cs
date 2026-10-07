using System.Net;
using Sia.Engine.Example;
using Xunit;

namespace Sia.Rendering.Debug.Tests;

public sealed class AssetInputTests
{
    private const string Address = "https://example.invalid/assets/test?version=2";

    [Fact]
    public async Task ExactLoaderReturnsTypedResultReleasesStreamsAndBorrowsClient()
    {
        using var handler = new PayloadHandler(() => new([2, 3, 5]));
        using var client = new HttpClient(handler);
        Stream? payload = null;
        var result = await AssetInput.LoadExactAsync(Address, client, 3, stream => {
            payload = stream;
            Assert.False(stream.CanWrite);
            return (stream.ReadByte(), stream.ReadByte(), stream.ReadByte(), stream.ReadByte());
        });
        Assert.Equal((2, 3, 5, -1), result);
        Assert.Throws<ObjectDisposedException>(() => payload!.ReadByte());
        Assert.True(handler.LastStream!.Disposed);
        Assert.Equal(Address, handler.LastAddress!.AbsoluteUri);

        Assert.Equal(2, await AssetInput.LoadExactAsync(Address, client, 3, stream => stream.ReadByte()));
        Assert.Equal(2, handler.Requests); // Loading does not own the reusable client.
    }

    [Fact]
    public async Task BoundedLoaderReturnsRetainedMemoryAfterReleasingInput()
    {
        using var handler = new PayloadHandler(() => new([2, 3, 5]));
        using var client = new HttpClient(handler);
        var result = await AssetInput.LoadBoundedAsync(Address, client, 8, static bytes => bytes);
        Assert.True(handler.LastStream!.Disposed);
        Assert.Equal(new byte[] { 2, 3, 5 }, result.ToArray());
    }

    [Theory]
    [InlineData(true, 2, typeof(EndOfStreamException))]
    [InlineData(true, 4, typeof(InvalidDataException))]
    [InlineData(false, 4, typeof(InvalidDataException))]
    public async Task SizeFailureReleasesInputWithoutDecoding(bool exact, int length, Type exception)
    {
        using var handler = new PayloadHandler(() => new(new byte[length]));
        using var client = new HttpClient(handler);
        var decodes = 0;
        await Assert.ThrowsAsync(exception, async () => {
            if (exact)
                await AssetInput.LoadExactAsync(Address, client, 3, _ => ++decodes);
            else
                await AssetInput.LoadBoundedAsync(Address, client, 3, _ => ++decodes);
        });
        Assert.Equal(0, decodes);
        Assert.True(handler.LastStream!.Disposed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DecoderFailureReleasesInputAndPreservesException(bool exact)
    {
        using var handler = new PayloadHandler(() => new([2, 3, 5]));
        using var client = new HttpClient(handler);
        var error = new InvalidDataException("Rejected by the asset codec.");
        var actual = await Assert.ThrowsAsync<InvalidDataException>(async () => {
            if (exact)
                await AssetInput.LoadExactAsync<int>(Address, client, 3, _ => throw error);
            else
                await AssetInput.LoadBoundedAsync<int>(Address, client, 3, _ => throw error);
        });
        Assert.Same(error, actual);
        Assert.True(handler.LastStream!.Disposed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CancellationDuringReadReleasesInputWithoutDecoding(bool exact)
    {
        using var cancellation = new CancellationTokenSource();
        using var handler = new PayloadHandler(() => new([2, 3, 5]) { BeforeRead = cancellation.Cancel });
        using var client = new HttpClient(handler);
        var decodes = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => {
            if (exact)
                await AssetInput.LoadExactAsync(Address, client, 3, _ => ++decodes, cancellation.Token);
            else
                await AssetInput.LoadBoundedAsync(Address, client, 3, _ => ++decodes, cancellation.Token);
        });
        Assert.Equal(0, decodes);
        Assert.True(handler.LastStream!.Disposed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InvalidConfigurationFailsBeforeOpeningSource(bool exact)
    {
        using var handler = new PayloadHandler(() => new([]));
        using var client = new HttpClient(handler);
        if (exact) {
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => AssetInput.LoadExactAsync(Address, client, -1, _ => 0));
            await Assert.ThrowsAsync<ArgumentNullException>(() => AssetInput.LoadExactAsync<int>(Address, client, 1, null!));
        }
        else {
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => AssetInput.LoadBoundedAsync(Address, client, -1, _ => 0));
            await Assert.ThrowsAsync<ArgumentNullException>(() => AssetInput.LoadBoundedAsync<int>(Address, client, 1, null!));
        }
        Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public void StreamAdapterUsesOnlyTheSelectedSliceAndReleasesItOnFailure()
    {
        byte[] storage = [99, 2, 3, 5, 100];
        Stream? payload = null;
        var error = new InvalidDataException("Rejected slice.");
        var actual = Assert.Throws<InvalidDataException>(() => AssetInput.Decode<int>(storage.AsMemory(1, 3), stream => {
            payload = stream;
            Assert.Equal(3, stream.Length);
            Assert.False(stream.CanWrite);
            Assert.Equal(2, stream.ReadByte());
            Assert.Equal(3, stream.ReadByte());
            Assert.Equal(5, stream.ReadByte());
            Assert.Equal(-1, stream.ReadByte());
            throw error;
        }));
        Assert.Same(error, actual);
        Assert.Throws<ObjectDisposedException>(() => payload!.ReadByte());
    }

    private sealed class PayloadHandler(Func<TrackingStream> create) : HttpMessageHandler
    {
        internal int Requests;
        internal Uri? LastAddress;
        internal TrackingStream? LastStream;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            LastAddress = request.RequestUri;
            LastStream = create();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(LastStream) });
        }
    }

    // Non-seekable one-byte reads exercise network fragmentation and bounded EOF detection.
    private sealed class TrackingStream(byte[] bytes) : Stream
    {
        private int _position;
        internal bool Disposed;
        internal Action? BeforeRead;
        public override bool CanRead => !Disposed;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            BeforeRead?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.Span));
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            ObjectDisposedException.ThrowIf(Disposed, this);
            if (buffer.IsEmpty || _position == bytes.Length) return 0;
            buffer[0] = bytes[_position++];
            return 1;
        }

        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
