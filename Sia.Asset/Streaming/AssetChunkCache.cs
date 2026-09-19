using System.Security.Cryptography;

namespace Sia.Asset;

public readonly record struct AssetChunkCacheStatistics(
    long ReservedBytes, long PeakReservedBytes, int ResidentChunks, int LoadingChunks, int QueuedChunks,
    long ReadBytes, long ReadAttempts, long CacheHits, long CoalescedRequests, long Evictions);

public sealed class AssetChunkCache : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Func<AssetChunk, CancellationToken, ValueTask<Stream>> _open;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly HashSet<Entry> _loading = [];
    private readonly long _budget;
    private readonly int _concurrency, _maximumPending, _maximumAttempts;
    private long _reserved, _peak, _clock, _readBytes, _attempts, _hits, _coalesced, _evictions;
    private bool _disposed;

    public AssetChunkCache(Func<AssetChunk, CancellationToken, ValueTask<Stream>> open,
        long byteBudget, int maximumConcurrentReads = 2, int maximumPendingChunks = 128, int maximumReadAttempts = 2)
    {
        ArgumentNullException.ThrowIfNull(open);
        ArgumentOutOfRangeException.ThrowIfLessThan(byteBudget, 2);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumConcurrentReads, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumPendingChunks, maximumConcurrentReads);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumReadAttempts, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumReadAttempts, 8);
        _open = open;
        _budget = byteBudget;
        _concurrency = maximumConcurrentReads;
        _maximumPending = maximumPendingChunks;
        _maximumAttempts = maximumReadAttempts;
    }

    public AssetChunkCacheStatistics Statistics
    {
        get {
            lock (_gate) {
                return new(_reserved, _peak, _entries.Values.Count(e => e.Data is not null), _loading.Count,
                    _entries.Values.Count(e => e.Data is null && !e.Loading), Interlocked.Read(ref _readBytes),
                    Interlocked.Read(ref _attempts), _hits, _coalesced, _evictions);
            }
        }
    }

    public async ValueTask<AssetChunkLease> AcquireAsync(AssetChunk chunk, int priority = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        cancellationToken.ThrowIfCancellationRequested();
        if ((long)chunk.Length + 1 > _budget) { throw new ArgumentException("Chunk cannot fit the payload budget.", nameof(chunk)); }
        Entry entry;
        lock (_gate) {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_entries.TryGetValue(chunk.Id, out entry!)) {
                if (entry.Chunk.Length != chunk.Length) { throw new ArgumentException("Conflicting chunk length for the same content ID.", nameof(chunk)); }
                if (entry.Data is not null) { _hits++; } else { _coalesced++; }
                entry.Priority = Math.Min(entry.Priority, priority);
            } else {
                if (_entries.Values.Count(e => e.Data is null) >= _maximumPending) {
                    throw new InvalidOperationException("The bounded chunk request queue is full.");
                }
                entry = new(chunk, priority, ++_clock);
                _entries.Add(chunk.Id, entry);
            }
            entry.Users++;
            entry.LastUse = ++_clock;
            Pump();
        }
        var transferred = false;
        try {
            var bytes = await entry.Ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate) { ObjectDisposedException.ThrowIf(_disposed, this); }
            var lease = new AssetChunkLease(bytes, () => Release(entry));
            transferred = true;
            return lease;
        }
        finally { if (!transferred) { Release(entry); } }
    }

    private void Pump()
    {
        if (_disposed) { return; }
        foreach (var entry in _entries.Values.Where(e => e.Data is null && !e.Loading)
            .OrderBy(e => e.Priority).ThenBy(e => e.Sequence).ToArray()) {
            if (_loading.Count >= _concurrency) { break; }
            var required = (long)entry.Chunk.Length + 1;
            byte[]? reusable = null;
            while (_reserved + required > _budget) {
                var victim = _entries.Values.Where(e => e.Data is not null && e.Users == 0).MinBy(e => e.LastUse);
                if (victim is null) { break; }
                _entries.Remove(victim.Chunk.Id);
                if (victim.Data!.Length == entry.Chunk.Length) { reusable ??= victim.Data; }
                victim.Data = null;
                _reserved -= (long)victim.Chunk.Length + 1;
                _evictions++;
            }
            if (_reserved + required > _budget) { continue; }
            entry.Reusable = reusable;
            _reserved += required;
            _peak = Math.Max(_peak, _reserved);
            entry.Loading = true;
            _loading.Add(entry);
            _ = LoadAsync(entry);
        }
    }

    private async Task LoadAsync(Entry entry)
    {
        await Task.Yield();
        byte[]? bytes = null;
        Exception? failure = null;
        try {
            var token = entry.Cancellation.Token;
            token.ThrowIfCancellationRequested();
            bytes = entry.Reusable ?? new byte[entry.Chunk.Length];
            entry.Reusable = null;
            var extra = new byte[1];
            for (var attempt = 1; ; attempt++) {
                try {
                    Interlocked.Increment(ref _attempts);
                    await using var stream = await _open(entry.Chunk, token).ConfigureAwait(false);
                    var offset = 0;
                    while (offset < bytes.Length) {
                        var read = await stream.ReadAsync(bytes.AsMemory(offset, Math.Min(65536, bytes.Length - offset)), token).ConfigureAwait(false);
                        if (read == 0) { throw new InvalidDataException("Truncated chunk payload."); }
                        Interlocked.Add(ref _readBytes, read);
                        offset += read;
                        // Bound uninterrupted CPU work even with a synchronously completing source in WASM.
                        await Task.Yield();
                    }
                    var trailing = await stream.ReadAsync(extra, token).ConfigureAwait(false);
                    Interlocked.Add(ref _readBytes, trailing);
                    if (trailing != 0) { throw new InvalidDataException("Chunk payload exceeds its declared length."); }
                    token.ThrowIfCancellationRequested();
                    if (!SHA256.HashData(bytes).AsSpan().SequenceEqual(Convert.FromHexString(entry.Chunk.Id))) {
                        throw new InvalidDataException("Chunk checksum mismatch.");
                    }
                    break;
                }
                catch (Exception error) when (attempt < _maximumAttempts && !token.IsCancellationRequested
                    && (error is IOException || error is HttpRequestException http
                        && (http.StatusCode is null || (int)http.StatusCode >= 500 || (int)http.StatusCode == 429))) {
                    await Task.Delay(TimeSpan.FromMilliseconds(25 * attempt), token).ConfigureAwait(false);
                }
            }
        }
        catch (Exception error) { failure = error; }
        lock (_gate) {
            entry.Reusable = null;
            _loading.Remove(entry);
            entry.Loading = false;
            if (failure is null && !_disposed && entry.Users != 0 && !entry.Cancellation.IsCancellationRequested) {
                entry.Data = bytes!;
                entry.Ready.TrySetResult(bytes!);
            } else {
                _reserved -= (long)entry.Chunk.Length + 1;
                RemoveCurrent(entry);
                if (failure is OperationCanceledException || entry.Users == 0) { entry.Ready.TrySetCanceled(); }
                else { entry.Ready.TrySetException(failure ?? new ObjectDisposedException(nameof(AssetChunkCache))); }
            }
            Pump();
        }
        await FinishCancellationAsync(entry).ConfigureAwait(false);
    }

    private void RemoveCurrent(Entry entry)
    {
        if (_entries.TryGetValue(entry.Chunk.Id, out var current) && ReferenceEquals(current, entry)) {
            _entries.Remove(entry.Chunk.Id);
        }
    }

    private void Release(Entry entry)
    {
        var finish = false;
        lock (_gate) {
            entry.Users--;
            var cancel = entry.Users == 0 && entry.Data is null;
            if (cancel) {
                RemoveCurrent(entry);
                RequestCancellation(entry);
                if (!entry.Loading) { entry.Ready.TrySetCanceled(); finish = true; }
            }
            if (entry.Users == 0 && entry.Data is not null && _disposed) {
                entry.Data = null;
                RemoveCurrent(entry);
                _reserved -= (long)entry.Chunk.Length + 1;
            }
            entry.LastUse = ++_clock;
            Pump();
        }
        if (finish) { _ = FinishCancellationAsync(entry); }
    }

    private static void RequestCancellation(Entry entry)
    {
        if (!entry.CancellationFinished) { entry.CancelCallbacks ??= entry.Cancellation.CancelAsync(); }
    }

    private async Task FinishCancellationAsync(Entry entry)
    {
        Task callbacks;
        lock (_gate) {
            if (entry.CancellationFinished) { return; }
            entry.CancellationFinished = true;
            callbacks = entry.CancelCallbacks ?? Task.CompletedTask;
        }
        try {
            await callbacks.ConfigureAwait(false);
            entry.Finished.TrySetResult();
        }
        catch (Exception error) { entry.Finished.TrySetException(error); }
        finally { entry.Cancellation.Dispose(); }
    }

    public async ValueTask DisposeAsync()
    {
        Entry[] loading;
        lock (_gate) {
            _disposed = true;
            loading = _loading.ToArray();
            foreach (var entry in loading) { RequestCancellation(entry); }
            foreach (var entry in _entries.Values.ToArray()) {
                if (entry.Data is null && !entry.Loading) {
                    _entries.Remove(entry.Chunk.Id);
                    entry.Ready.TrySetException(new ObjectDisposedException(nameof(AssetChunkCache)));
                    _ = FinishCancellationAsync(entry);
                } else if (entry.Data is not null && entry.Users == 0) {
                    _entries.Remove(entry.Chunk.Id);
                    entry.Data = null;
                    _reserved -= (long)entry.Chunk.Length + 1;
                }
            }
        }
        await Task.WhenAll(loading.Select(e => e.Finished.Task)).ConfigureAwait(false);
        // Existing leases remain readable until their owners release them.
    }

    private sealed class Entry(AssetChunk chunk, int priority, long sequence)
    {
        public AssetChunk Chunk { get; } = chunk;
        public long Sequence { get; } = sequence;
        public int Priority = priority;
        public int Users;
        public long LastUse;
        public bool Loading;
        public bool CancellationFinished;
        public Task? CancelCallbacks;
        public byte[]? Data;
        public byte[]? Reusable;
        public CancellationTokenSource Cancellation { get; } = new();
        public TaskCompletionSource<byte[]> Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

public sealed class AssetChunkLease : IDisposable
{
    private Action? _release;
    private ReadOnlyMemory<byte> _memory;
    internal AssetChunkLease(byte[] bytes, Action release) { _memory = bytes; _release = release; }
    public ReadOnlyMemory<byte> Memory {
        get { ObjectDisposedException.ThrowIf(_release is null, this); return _memory; }
    }
    public void Dispose()
    {
        var release = Interlocked.Exchange(ref _release, null);
        if (release is null) { return; }
        _memory = default;
        release();
    }
}
