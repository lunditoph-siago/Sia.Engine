namespace Sia.Asset;

/// <summary>Prepares a record from bounded chunk IO. Callers separately acquire it on the World owner context.</summary>
/// <remarks>
/// The encoded input buffer has a per-load limit, separate from cache and decoder budgets.
/// Cancellation stops preparation; an already started synchronous decoder completes normally.
/// No decoded-record cache, World scheduling or automatic dependency acquisition is provided.
/// </remarks>
public static class ChunkLoader
{
    public const int DefaultMaximumBytes = 128 * 1024 * 1024;

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
        if (content.Length > maximumBytes)
            throw new InvalidDataException("Encoded asset exceeds its preparation byte limit.");

        // Synchronous borrowed-stream codecs need a contiguous input. This buffer
        // is separate from cache/decoded-data budgets, and belongs to this load only.
        var bytes = GC.AllocateUninitializedArray<byte>((int)content.Length);
        var offset = 0;
        foreach (var chunk in content.Chunks) {
            using var lease = await cache.AcquireAsync(chunk, priority, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            lease.Memory.Span.CopyTo(bytes.AsSpan(offset, chunk.Length));
            offset += chunk.Length;
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new MemoryStream(bytes, writable: false);
    }
}
