using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Sia.Asset;
using Xunit;

namespace Sia.Rendering.Debug.Tests;

public sealed class AssetPackTests
{
    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static (AssetPackIndex Index, AssetChunk Pack, AssetChunk First, AssetChunk Second, byte[] Bytes) Fixture()
    {
        byte[] a = [1, 2, 3, 4]; byte[] b = [5, 6, 7];
        var bytes = a.Concat(b).ToArray(); var pack = AssetChunk.FromBytes(bytes);
        var first = AssetChunk.FromBytes(a); var second = AssetChunk.FromBytes(b);
        return (new([
            new("a.siaasset", first, new(pack, 0)), new("b.chunk", second, new(pack, a.Length)),
            new("alias.siaasset", first, new(pack, 0))]), pack, first, second, bytes);
    }

    private static (byte[] Bytes, string Hash) Encode(AssetPackIndex index)
    {
        using var output = new MemoryStream(); var hash = index.Save(output);
        return (output.ToArray(), hash);
    }

    [Fact]
    public void IndexRoundTripAuthenticatesExactBytesAndIsDeterministic()
    {
        var fixture = Fixture(); var encoded = Encode(fixture.Index);
        Assert.Equal(fixture.Index.EncodedBytes, encoded.Bytes.Length);
        Assert.Equal(Hash(encoded.Bytes), encoded.Hash);
        var loaded = AssetPackIndex.Load(new FragmentedStream(encoded.Bytes), encoded.Hash);
        Assert.Equal(3, loaded.Entries.Count);
        Assert.Equal(fixture.First.Id, loaded.Entries["a.siaasset"].Chunk.Id);
        Assert.Equal(fixture.Pack.Id, loaded.Resolve(fixture.First)!.Value.Pack.Id);
        Assert.Equal(4, loaded.Resolve(fixture.Second)!.Value.Offset);
        Assert.Same(loaded.Entries["a.siaasset"].Slice.Pack, loaded.Entries["b.chunk"].Slice.Pack);
        Assert.Same(loaded.Entries["a.siaasset"].Slice.Pack.Id, loaded.Entries["b.chunk"].Slice.Pack.Id);
        Assert.Same(loaded.Entries["a.siaasset"].Chunk, loaded.Entries["alias.siaasset"].Chunk);
        Assert.Null(loaded.Resolve(AssetChunk.FromBytes(new byte[] { 99 })));
        var roundTrip = Encode(loaded);
        Assert.Equal(encoded.Hash, roundTrip.Hash); Assert.Equal(encoded.Bytes, roundTrip.Bytes);
        var reordered = Encode(new AssetPackIndex(fixture.Index.Entries.Values.Reverse()));
        Assert.Equal(encoded.Hash, reordered.Hash); Assert.Equal(encoded.Bytes, reordered.Bytes);
    }

    [Fact]
    public void EmptyIndexStillRequiresItsCompleteAuthenticatedEnvelopeAndAdmission()
    {
        var encoded = Encode(new AssetPackIndex([])); Assert.Equal(48, encoded.Bytes.Length);
        Assert.Empty(AssetPackIndex.Load(new FragmentedStream(encoded.Bytes), encoded.Hash, 48).Entries);
        Assert.Throws<InvalidDataException>(() => AssetPackIndex.Load(new FragmentedStream(encoded.Bytes), encoded.Hash, 47));
        Assert.Throws<InvalidDataException>(() => AssetPackIndex.Load(new FragmentedStream(encoded.Bytes.Concat(new byte[] { 1 }).ToArray()), encoded.Hash));
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("a:b")]
    [InlineData("..")]
    [InlineData("CON.siaasset")]
    [InlineData("com1.chunk")]
    [InlineData("a.")]
    [InlineData("a b")]
    public void UnsafePortableFilenamesAreRejected(string name)
    {
        var fixture = Fixture();
        Assert.Throws<ArgumentException>(() => new AssetPackIndex([new(name, fixture.First, new(fixture.Pack, 0))]));
    }

    [Fact]
    public void ConflictingNamesLocationsLengthsAndOverlapsAreRejected()
    {
        var f = Fixture(); var first = f.Index.Entries["a.siaasset"];
        Assert.Throws<ArgumentException>(() => new AssetPackIndex([first, first]));
        Assert.Throws<ArgumentException>(() => new AssetPackIndex([first, first with { FileName = "A.siaasset" }]));
        Assert.Throws<ArgumentException>(() => new AssetPackIndex([first,
            new("other", f.First, new(f.Pack, 1))]));
        Assert.Throws<ArgumentException>(() => new AssetPackIndex([first,
            new("other", new AssetChunk(f.First.Id, 3), new(f.Pack, 0))]));
        Assert.Throws<ArgumentException>(() => new AssetPackIndex([first,
            new("other", f.Second, new(f.Pack, 3))]));
        Assert.Throws<ArgumentException>(() => new AssetPackIndex([
            new("other", f.First, new(f.Pack, 4))]));
        Assert.Throws<ArgumentException>(() => new AssetChunkSlice(f.Pack, int.MaxValue));
        Assert.Throws<ArgumentException>(() => new AssetPackIndex([new("other", f.First, default)]));
        Assert.Throws<InvalidDataException>(() => f.Index.Resolve(new AssetChunk(f.First.Id, 3)));
    }

    [Fact]
    public void EntriesSnapshotTheInputAndCannotBeMutatedThroughThePublicDictionary()
    {
        var f = Fixture(); var entries = f.Index.Entries.Values.ToList();
        var index = new AssetPackIndex(entries); entries.Clear();
        Assert.Equal(3, index.Entries.Count);
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, AssetPackEntry>)index.Entries).Clear());
    }

    [Fact]
    public void IndexRejectsTruncationTrailingBytesChecksumAndWrongExpectedIdentity()
    {
        var encoded = Encode(Fixture().Index);
        Assert.Throws<EndOfStreamException>(() => AssetPackIndex.Load(new FragmentedStream(encoded.Bytes[..^1]), encoded.Hash));
        Assert.Throws<InvalidDataException>(() => AssetPackIndex.Load(new FragmentedStream(encoded.Bytes.Concat(new byte[] { 0 }).ToArray()), encoded.Hash));
        var corrupted = encoded.Bytes.ToArray(); corrupted[^1] ^= 1;
        Assert.Throws<InvalidDataException>(() => AssetPackIndex.Load(new FragmentedStream(corrupted), Hash(corrupted)));
        Assert.Throws<InvalidDataException>(() => AssetPackIndex.Load(new FragmentedStream(encoded.Bytes), new string('F', 64)));
        Assert.Throws<InvalidDataException>(() => AssetPackIndex.Load(new FragmentedStream(encoded.Bytes), encoded.Hash, encoded.Bytes.Length - 1));
    }

    [Fact]
    public void AuthenticatedMaliciousCountsOffsetsNamesAndDuplicateEntriesAreStillRejected()
    {
        var f = Fixture(); var original = Encode(f.Index).Bytes;
        var bomb = original.ToArray(); BinaryPrimitives.WriteInt32LittleEndian(bomb.AsSpan(8), int.MaxValue); RehashFooter(bomb);
        Assert.Throws<InvalidDataException>(() => AssetPackIndex.Load(new FragmentedStream(bomb), Hash(bomb)));
        var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(original.AsSpan(16));
        var badOffset = original.ToArray(); BinaryPrimitives.WriteInt32LittleEndian(badOffset.AsSpan(18 + nameLength + 72), int.MaxValue); RehashFooter(badOffset);
        Assert.Throws<InvalidDataException>(() => AssetPackIndex.Load(new FragmentedStream(badOffset), Hash(badOffset)));
        var badName = original.ToArray(); badName[18] = (byte)'/'; RehashFooter(badName);
        Assert.Throws<InvalidDataException>(() => AssetPackIndex.Load(new FragmentedStream(badName), Hash(badName)));
        var highName = original.ToArray(); highName[18] = 128; RehashFooter(highName);
        Assert.Throws<InvalidDataException>(() => AssetPackIndex.Load(new FragmentedStream(highName), Hash(highName)));
        var two = Encode(new AssetPackIndex([new("a", f.First, new(f.Pack, 0)), new("b", f.Second, new(f.Pack, 4))])).Bytes;
        two[18 + 79] = (byte)'a'; RehashFooter(two);
        Assert.Throws<InvalidDataException>(() => AssetPackIndex.Load(new FragmentedStream(two), Hash(two)));
    }

    [Fact]
    public async Task PackedLeavesShareOnePhysicalBufferReservationAndKeepItPinnedIndependently()
    {
        var f = Fixture(); var opens = 0;
        await using var cache = new AssetChunkCache((chunk, _) => {
            Assert.Equal(f.Pack.Id, chunk.Id); Interlocked.Increment(ref opens);
            return ValueTask.FromResult<Stream>(new MemoryStream(f.Bytes));
        }, f.Bytes.Length + 1, resolve: f.Index.Resolve);
        var a = cache.AcquireAsync(f.First).AsTask(); var b = cache.AcquireAsync(f.Second).AsTask();
        using var first = await a; using var second = await b;
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, first.Memory.ToArray());
        Assert.Equal(new byte[] { 5, 6, 7 }, second.Memory.ToArray());
        Assert.True(MemoryMarshal.TryGetArray(first.Memory, out var firstArray));
        Assert.True(MemoryMarshal.TryGetArray(second.Memory, out var secondArray));
        Assert.Same(firstArray.Array, secondArray.Array);
        Assert.Equal(4, secondArray.Offset - firstArray.Offset);
        Assert.Equal(1, opens);
        Assert.Equal(f.Bytes.Length + 1, cache.Statistics.PeakReservedBytes);
        await cache.DisposeAsync();
        Assert.Equal(f.Bytes.Length + 1, cache.Statistics.ReservedBytes);
        first.Dispose(); Assert.Equal(f.Bytes.Length + 1, cache.Statistics.ReservedBytes);
        Assert.Equal(5, second.Memory.Span[0]);
        second.Dispose(); Assert.Equal(0, cache.Statistics.ReservedBytes);
    }

    [Fact]
    public async Task BorrowedRawDecoderSeesOnlyItsSliceAndEofRatherThanAdjacentPackedFiles()
    {
        var f = Fixture();
        await using var cache = new AssetChunkCache((_, _) => ValueTask.FromResult<Stream>(new MemoryStream(f.Bytes)), 8,
            resolve: f.Index.Resolve);
        var content = new AssetContent<BytesRecord>(AssetId.New(), [f.Second], "packed-leaf");

        var record = await ChunkLoader.LoadAsync(content, cache);

        Assert.Equal(new byte[] { 5, 6, 7 }, record.Data); Assert.Equal("packed-leaf", record.Name);
        await cache.DisposeAsync(); Assert.Equal(0, cache.Statistics.ReservedBytes);
    }

    [Fact]
    public async Task PackedWireSlicePreservesCallerLz4MetadataAndUnpackedFallback()
    {
        var raw = Enumerable.Repeat((byte)19, 4096).ToArray(); var compressed = AssetChunk.Encode(raw);
        Assert.Equal(AssetChunkEncoding.Lz4, compressed.Chunk.Encoding);
        byte[] suffix = [72, 91]; var packBytes = compressed.Bytes.Concat(suffix).ToArray();
        var pack = AssetChunk.FromBytes(packBytes); var fallback = AssetChunk.FromBytes(suffix);
        var index = new AssetPackIndex([new("encoded.chunk", AssetChunk.FromBytes(compressed.Bytes), new(pack, 0))]);
        var bodies = new Dictionary<string, byte[]> { [pack.Id] = packBytes, [fallback.Id] = suffix };
        await using var cache = new AssetChunkCache((chunk, _) => ValueTask.FromResult<Stream>(new MemoryStream(bodies[chunk.Id])), 8192,
            resolve: index.Resolve);
        using var encoded = await cache.AcquireAsync(compressed.Chunk);
        var decoded = new byte[raw.Length]; compressed.Chunk.Decode(encoded.Memory.Span, decoded);
        Assert.Equal(raw, decoded);
        using var normal = await cache.AcquireAsync(fallback); Assert.Equal(suffix, normal.Memory.ToArray());
    }

    [Fact]
    public async Task PackBudgetIsAdmittedBeforeSourceAndUsesPhysicalRatherThanLeafBytes()
    {
        var f = Fixture(); var opens = 0;
        await using var cache = new AssetChunkCache((_, _) => {
            opens++; return ValueTask.FromResult<Stream>(Stream.Null);
        }, f.First.Length + 1, resolve: f.Index.Resolve);
        await Assert.ThrowsAsync<ArgumentException>(() => cache.AcquireAsync(f.First).AsTask());
        Assert.Equal(0, opens); Assert.Equal(0, cache.Statistics.ReservedBytes);
    }

    [Fact]
    public async Task PackAndLeafHashesAreVerifiedIndependently()
    {
        var f = Fixture(); var corrupted = f.Bytes.ToArray(); corrupted[0] ^= 1;
        await using var corruptPack = new AssetChunkCache((_, _) => ValueTask.FromResult<Stream>(new MemoryStream(corrupted)), 8,
            maximumReadAttempts: 1, resolve: f.Index.Resolve);
        await Assert.ThrowsAsync<InvalidDataException>(() => corruptPack.AcquireAsync(f.First).AsTask());
        var lie = new AssetChunk(new string('F', 64), f.First.Length);
        var index = new AssetPackIndex([new("lie", lie, new(f.Pack, 0))]);
        await using var corruptLeaf = new AssetChunkCache((_, _) => ValueTask.FromResult<Stream>(new MemoryStream(f.Bytes)), 8,
            maximumReadAttempts: 1, resolve: index.Resolve);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => corruptLeaf.AcquireAsync(lie).AsTask());
        Assert.Equal("Packed leaf checksum mismatch.", error.Message);
        await corruptPack.DisposeAsync(); await corruptLeaf.DisposeAsync();
        Assert.Equal(0, corruptPack.Statistics.ReservedBytes); Assert.Equal(0, corruptLeaf.Statistics.ReservedBytes);
    }

    [Fact]
    public async Task CancellingOnePackedLeafDoesNotCancelAnotherUserOfTheSamePack()
    {
        var f = Fixture(); var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var opens = 0;
        await using var cache = new AssetChunkCache(async (_, token) => {
            Interlocked.Increment(ref opens); opened.SetResult(); await release.Task.WaitAsync(token);
            return new MemoryStream(f.Bytes);
        }, 8, resolve: f.Index.Resolve);
        using var cancellation = new CancellationTokenSource();
        var first = cache.AcquireAsync(f.First, cancellationToken: cancellation.Token).AsTask();
        var second = cache.AcquireAsync(f.Second).AsTask();
        await opened.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        release.SetResult(); using var remaining = await second.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, opens); Assert.Equal(new byte[] { 5, 6, 7 }, remaining.Memory.ToArray());
        remaining.Dispose(); await cache.DisposeAsync(); Assert.Equal(0, cache.Statistics.ReservedBytes);
    }

    private static void RehashFooter(byte[] bytes) => SHA256.HashData(bytes.AsSpan(0, bytes.Length - 32)).CopyTo(bytes, bytes.Length - 32);

    private sealed record BytesRecord(byte[] Data) : AssetRecord, ILoadable<BytesRecord>
    {
        public static BytesRecord Load(Stream stream, string? name = null)
        {
            var bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes);
            Assert.Equal(-1, stream.ReadByte()); return new(bytes) { Name = name };
        }
    }

    private sealed class FragmentedStream(byte[] bytes) : Stream
    {
        private int _position;
        public override int Read(Span<byte> destination)
        {
            var length = System.Math.Min(3, System.Math.Min(destination.Length, bytes.Length - _position));
            bytes.AsSpan(_position, length).CopyTo(destination); _position += length; return length;
        }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override bool CanRead => true;
        public override bool CanWrite => false;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
