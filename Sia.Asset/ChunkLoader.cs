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

    private static async ValueTask<MemoryStream> ReadAsync<TRecord>(AssetContent<TRecord> content,
        AssetChunkCache cache, int maximumBytes, int priority, CancellationToken cancellationToken)
        where TRecord : class, IAssetRecord
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumBytes);
        cancellationToken.ThrowIfCancellationRequested();
        if (content.Length > maximumBytes || content.DecodedLength > maximumBytes)
            throw new InvalidDataException("Encoded asset exceeds its preparation byte limit.");

        // Synchronous borrowed-stream codecs need a contiguous input. This buffer
        // is separate from cache/decoded-data budgets, and belongs to this load only.
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
}
