using Sia.Asset;
using Xunit;

namespace Sia.Rendering.Debug.Tests;

public sealed class ChunkLoaderLeaseTests
{
    private sealed record Probe(byte[] Bytes) : AssetRecord,
        ILoadable<Probe>, ILoadable<Probe, Func<Stream, Probe>>
    {
        public static Probe Load(Stream stream, string? name = null)
        {
            using var output = new MemoryStream(); stream.CopyTo(output);
            return new(output.ToArray()) { Name = name };
        }

        public static Probe Load(Stream stream, Func<Stream, Probe> decode, string? name = null) => decode(stream);
    }

    private static AssetContent<Probe> Content(params AssetChunk[] chunks) => new(AssetId.New(), chunks, "probe");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SingleRawPinsItsCacheReservationUntilBorrowedDecoderFinishes(bool fail)
    {
        byte[] bytes = [1, 2, 3, 4, 5];
        await using var cache = new AssetChunkCache((_, _) => ValueTask.FromResult<Stream>(new MemoryStream(bytes)), 6);
        Stream? borrowed = null;
        Func<Stream, Probe> decode = stream => {
            borrowed = stream;
            // Cache disposal cannot recycle a buffer while the synchronous codec borrows it.
            cache.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Assert.Equal(6, cache.Statistics.ReservedBytes);
            Assert.True(stream.CanSeek);
            Assert.Equal(5, stream.Length);
            Assert.Equal(1, stream.ReadByte());
            Assert.Equal(4, stream.Seek(-1, SeekOrigin.End));
            Assert.Equal(5, stream.ReadByte());
            Assert.Equal(-1, stream.ReadByte());
            stream.Position = 0;
            var result = Probe.Load(stream);
            Assert.Equal(bytes, result.Bytes);
            Assert.Throws<NotSupportedException>(() => stream.WriteByte(7));
            if (fail) throw new InvalidDataException("decoder failure");
            return result;
        };

        if (fail) {
            var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                ChunkLoader.LoadAsync(Content(AssetChunk.FromBytes(bytes)), cache, decode).AsTask());
            Assert.Equal("decoder failure", error.Message);
        }
        else {
            var result = await ChunkLoader.LoadAsync(Content(AssetChunk.FromBytes(bytes)), cache, decode);
            Assert.Equal(bytes, result.Bytes);
        }
        Assert.NotNull(borrowed);
        Assert.False(borrowed.CanRead);
        Assert.Throws<ObjectDisposedException>(() => borrowed.ReadByte());
        Assert.Equal(0, cache.Statistics.ReservedBytes);
    }

    [Fact]
    public async Task CancellationFromBorrowedDecoderReleasesRawLease()
    {
        byte[] bytes = [13, 21, 34];
        await using var cache = new AssetChunkCache((_, _) => ValueTask.FromResult<Stream>(new MemoryStream(bytes)), 4);
        using var cancellation = new CancellationTokenSource();
        Func<Stream, Probe> decode = _ => {
            cache.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Assert.Equal(4, cache.Statistics.ReservedBytes);
            cancellation.Cancel(); cancellation.Token.ThrowIfCancellationRequested();
            throw new InvalidOperationException("unreachable");
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ChunkLoader.LoadAsync(Content(AssetChunk.FromBytes(bytes)), cache, decode,
                cancellationToken: cancellation.Token).AsTask());

        Assert.Equal(0, cache.Statistics.ReservedBytes);
    }

    [Fact]
    public async Task CancellationAfterDecoderConstructsOwnedOutputDoesNotDiscardItsResult()
    {
        byte[] bytes = [2, 7, 1, 8];
        await using var cache = new AssetChunkCache((_, _) => ValueTask.FromResult<Stream>(new MemoryStream(bytes)), 5);
        using var cancellation = new CancellationTokenSource();
        Func<Stream, Probe> decode = stream => {
            var owned = Probe.Load(stream); cancellation.Cancel(); return owned;
        };

        var record = await ChunkLoader.LoadAsync(Content(AssetChunk.FromBytes(bytes)), cache, decode,
            cancellationToken: cancellation.Token);

        Assert.Equal(bytes, record.Bytes);
        await cache.DisposeAsync(); Assert.Equal(0, cache.Statistics.ReservedBytes);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public async Task RawFastPathStillAuthenticatesHashLengthAndEofBeforeDecode(int lengthChange)
    {
        byte[] expected = [1, 2, 3, 4];
        var supplied = lengthChange switch { -1 => new byte[] { 1, 2, 3 }, 0 => [1, 2, 3, 8], _ => [1, 2, 3, 4, 5] };
        await using var cache = new AssetChunkCache((_, _) => ValueTask.FromResult<Stream>(new MemoryStream(supplied)), 8,
            maximumReadAttempts: 1);
        var decoded = false;
        Func<Stream, Probe> decode = stream => { decoded = true; return Probe.Load(stream); };

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ChunkLoader.LoadAsync(Content(AssetChunk.FromBytes(expected)), cache, decode).AsTask());

        Assert.False(decoded);
        await cache.DisposeAsync();
        Assert.Equal(0, cache.Statistics.ReservedBytes);
    }

    [Fact]
    public async Task CancellationDuringSourceReadDoesNotLeakReservationOrEnterDecoder()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var never = new TaskCompletionSource<Stream>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var cache = new AssetChunkCache(async (_, token) => {
            entered.SetResult(); return await never.Task.WaitAsync(token);
        }, 8);
        using var cancellation = new CancellationTokenSource();
        var loading = ChunkLoader.LoadAsync(Content(AssetChunk.FromBytes(new byte[] { 1, 2, 3 })), cache,
            cancellationToken: cancellation.Token).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loading);
        await cache.DisposeAsync();

        Assert.Equal(0, cache.Statistics.ReservedBytes);
    }

    [Fact]
    public async Task AdmissionRejectsBeforeOpeningEvenForRawFastPath()
    {
        var opens = 0;
        await using var cache = new AssetChunkCache((_, _) => {
            opens++; return ValueTask.FromResult<Stream>(Stream.Null);
        }, 8);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ChunkLoader.LoadAsync(Content(AssetChunk.FromBytes(new byte[] { 1, 2, 3 })), cache, maximumBytes: 2).AsTask());
        Assert.Equal(0, opens);
        Assert.Equal(0, cache.Statistics.ReservedBytes);
    }

    [Fact]
    public async Task MultiChunkAndLz4KeepContiguousDecodeAndExactBytes()
    {
        var plain = Enumerable.Repeat((byte)37, 4096).ToArray();
        var compressed = AssetChunk.Encode(plain);
        Assert.Equal(AssetChunkEncoding.Lz4, compressed.Chunk.Encoding);
        byte[] prefix = [3, 1, 4];
        var raw = AssetChunk.FromBytes(prefix);
        var sources = new Dictionary<string, byte[]> { [raw.Id] = prefix, [compressed.Chunk.Id] = compressed.Bytes };
        await using var cache = new AssetChunkCache((chunk, _) =>
            ValueTask.FromResult<Stream>(new MemoryStream(sources[chunk.Id])), 8192);

        var result = await ChunkLoader.LoadAsync(Content(raw, compressed.Chunk), cache);

        Assert.Equal(prefix.Concat(plain), result.Bytes);
        Assert.Equal("probe", result.Name);
        await cache.DisposeAsync();
        Assert.Equal(0, cache.Statistics.ReservedBytes);
    }
}
