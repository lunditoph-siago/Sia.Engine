namespace Sia.Asset;

public static class ChunkLoader
{
    public const int DefaultMaximumBytes = 128 * 1024 * 1024;

    public static async ValueTask<TRecord> LoadStreamingAsync<TRecord, TOptions>(AssetContent<TRecord> content,
        AssetChunkCache cache, TOptions options, int maximumBytes = DefaultMaximumBytes, int priority = 0,
        CancellationToken cancellationToken = default)
        where TRecord : class, IAssetRecord, IAsyncLoadable<TRecord, TOptions>
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumBytes);
        if (content.Length > maximumBytes || content.DecodedLength > maximumBytes)
            throw new InvalidDataException("Encoded or decoded asset exceeds its preparation byte limit.");
        await using var stream = new ChunkReadStream(content.Chunks, cache, priority, cancellationToken);
        // As with ILoadable, the typed codec validates full consumption before
        // constructing its result. Do not discard a successfully returned record
        // if cancellation arrives after its potentially resource-owning construction.
        return await TRecord.LoadAsync(stream, options, content.Name, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Decoder returned no asset record.");
    }

    public static async ValueTask<TRecord> LoadAsync<TRecord>(AssetContent<TRecord> content,
        AssetChunkCache cache, int maximumBytes = DefaultMaximumBytes, int priority = 0,
        CancellationToken cancellationToken = default)
        where TRecord : class, IAssetRecord, ILoadable<TRecord>
    {
        using var stream = await ReadAsync(content, cache, maximumBytes, priority, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return TRecord.Load(stream, content.Name) ?? throw new InvalidDataException("Decoder returned no asset record.");
    }

    public static async ValueTask<TRecord> LoadAsync<TRecord, TOptions>(AssetContent<TRecord> content,
        AssetChunkCache cache, TOptions options, int maximumBytes = DefaultMaximumBytes, int priority = 0,
        CancellationToken cancellationToken = default)
        where TRecord : class, IAssetRecord, ILoadable<TRecord, TOptions>
    {
        using var stream = await ReadAsync(content, cache, maximumBytes, priority, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return TRecord.Load(stream, options, content.Name) ?? throw new InvalidDataException("Decoder returned no asset record.");
    }

    private static async ValueTask<Stream> ReadAsync<TRecord>(AssetContent<TRecord> content,
        AssetChunkCache cache, int maximumBytes, int priority, CancellationToken cancellationToken)
        where TRecord : class, IAssetRecord
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumBytes);
        cancellationToken.ThrowIfCancellationRequested();
        if (content.Length > maximumBytes || content.DecodedLength > maximumBytes)
            throw new InvalidDataException("Encoded asset exceeds its preparation byte limit.");

        // A single authenticated Raw chunk is already contiguous. Keep its cache
        // lease pinned until the borrowed-stream codec finishes instead of copying it.
        if (content.Chunks.Length == 1 && content.Chunks[0].Encoding == AssetChunkEncoding.Raw) {
            var lease = await cache.AcquireAsync(content.Chunks[0], priority, cancellationToken).ConfigureAwait(false);
            try {
                cancellationToken.ThrowIfCancellationRequested();
                return new LeaseReadStream(lease);
            }
            catch { lease.Dispose(); throw; }
        }

        // Other synchronous borrowed-stream codecs need a contiguous decoded input.
        // This buffer is separate from cache/decoded-data budgets and owned by this load.
        var bytes = GC.AllocateUninitializedArray<byte>((int)content.DecodedLength);
        var offset = 0;
        foreach (var chunk in content.Chunks) {
            using var lease = await cache.AcquireAsync(chunk, priority, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            chunk.Decode(lease.Memory.Span, bytes.AsSpan(offset, chunk.DecodedLength));
            offset += chunk.DecodedLength;
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new MemoryStream(bytes, writable: false);
    }

    private sealed class LeaseReadStream(AssetChunkLease lease) : Stream
    {
        private AssetChunkLease? _lease = lease;
        private ReadOnlyMemory<byte> _memory = lease.Memory;
        private int _position;

        public override bool CanRead => _lease is not null;
        public override bool CanSeek => _lease is not null;
        public override bool CanWrite => false;
        public override long Length { get { CheckOpen(); return _memory.Length; } }
        public override long Position {
            get { CheckOpen(); return _position; }
            set {
                CheckOpen();
                ArgumentOutOfRangeException.ThrowIfNegative(value);
                ArgumentOutOfRangeException.ThrowIfGreaterThan(value, int.MaxValue);
                _position = (int)value;
            }
        }

        public override int Read(Span<byte> buffer)
        {
            CheckOpen();
            var count = Math.Min(buffer.Length, Math.Max(0, _memory.Length - _position));
            if (count != 0) _memory.Span.Slice(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            return Read(buffer.AsSpan(offset, count));
        }
        public override int ReadByte()
        {
            CheckOpen();
            return _position < _memory.Length ? _memory.Span[_position++] : -1;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            CheckOpen();
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.Span));
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override long Seek(long offset, SeekOrigin origin)
        {
            CheckOpen();
            var target = origin switch {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => checked(_position + offset),
                SeekOrigin.End => checked(_memory.Length + offset),
                _ => throw new ArgumentException("Invalid seek origin.", nameof(origin))
            };
            if (target < 0) throw new IOException("Cannot seek before the beginning of the stream.");
            Position = target;
            return target;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) {
                _memory = default;
                Interlocked.Exchange(ref _lease, null)?.Dispose();
            }
            base.Dispose(disposing);
        }

        private void CheckOpen() => ObjectDisposedException.ThrowIf(_lease is null, this);
        public override void Flush() => CheckOpen();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
