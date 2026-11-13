using System.Runtime.ExceptionServices;

namespace Sia.Asset;

/// <summary>Shares an asynchronous preparation and its value for as long as a key has consumers.</summary>
/// <remarks>
/// The factory starts on the first caller's context and owns its I/O and worker dispatch.
/// A new request for a retiring key waits for that value's cleanup before preparing a replacement.
/// </remarks>
public sealed class AssetLeasePool<TKey, TValue> : IAsyncDisposable
    where TKey : notnull
    where TValue : class, IAsyncDisposable
{
    private readonly Lock _gate = new();
    private readonly Func<TKey, CancellationToken, Task<TValue>> _prepare;
    private readonly Dictionary<TKey, Entry> _entries = [];
    private readonly HashSet<Entry> _owned = [];
    private TaskCompletionSource? _close;
    private Exception? _cleanupError;

    public AssetLeasePool(Func<TKey, CancellationToken, Task<TValue>> prepare)
    {
        ArgumentNullException.ThrowIfNull(prepare);
        _prepare = prepare;
    }

    public async ValueTask<Lease> AcquireAsync(TKey key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        while (true) {
            cancellationToken.ThrowIfCancellationRequested();
            Entry entry;
            bool start;
            Task<Exception?>? retired = null;
            lock (_gate) {
                ObjectDisposedException.ThrowIf(_close is not null, this);
                start = !_entries.TryGetValue(key, out entry!);
                if (start) {
                    entry = new(key);
                    _entries.Add(key, entry);
                    _owned.Add(entry);
                }
                if (!start && (entry.References == 0 || entry.Ready.Task.IsFaulted || entry.Ready.Task.IsCanceled))
                    retired = entry.Drained.Task;
                else entry.References++;
            }
            if (retired is not null) {
                await retired.WaitAsync(cancellationToken);
                continue;
            }
            if (start) _ = PrepareAsync(entry);
            try {
                var value = await entry.Ready.Task.WaitAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                lock (_gate) {
                    ObjectDisposedException.ThrowIf(_close is not null, this);
                    return new(this, entry, value);
                }
            }
            catch {
                Release(entry);
                throw;
            }
        }
    }

    /// <summary>Close admissions, cancel pending preparations, and join cleanup after every delivered lease is released.</summary>
    public ValueTask DisposeAsync()
    {
        lock (_gate) {
            if (_close is { } existing) return new(existing.Task);
            _close = new(TaskCreationOptions.RunContinuationsAsynchronously);
            foreach (var entry in _owned) CancelPreparation(entry);
            CompleteCloseIfDrained();
            return new(_close.Task);
        }
    }

    private async Task PrepareAsync(Entry entry)
    {
        try {
            var token = entry.Stop.Token;
            token.ThrowIfCancellationRequested();
            var value = await _prepare(entry.Key, token);
            entry.Ready.SetResult(value ?? throw new InvalidOperationException("Asset preparation returned no value."));
        }
        catch (Exception error) {
            if (error is OperationCanceledException cancelled) entry.Ready.SetCanceled(cancelled.CancellationToken);
            else entry.Ready.SetException(error);
        }
    }

    private void Release(Entry entry)
    {
        Task? cancellation;
        lock (_gate) {
            cancellation = BeginRelease(entry);
        }
        if (cancellation is not null) _ = DrainAsync(entry, cancellation);
    }

    private static Task? BeginRelease(Entry entry)
        => --entry.References == 0 ? CancelPreparation(entry) : null;

    private static Task CancelPreparation(Entry entry)
        => entry.Cancellation ?? (entry.Ready.Task.IsCompleted ? Task.CompletedTask : entry.Cancellation = entry.Stop.CancelAsync());

    private async Task DrainAsync(Entry entry, Task cancellation)
    {
        Exception? failure = null;
        TValue? value = null;
        try { value = await entry.Ready.Task.ConfigureAwait(false); }
        catch (Exception) { /* Preparation failure is reported to its waiters. */ }
        try { await cancellation.ConfigureAwait(false); }
        catch (Exception error) { failure = error; }
        try { if (value is not null) await value.DisposeAsync().ConfigureAwait(false); }
        catch (Exception error) { failure = failure is null ? error : new AggregateException(failure, error); }
        lock (_gate) {
            RemoveIndex(entry);
            _owned.Remove(entry);
            entry.Stop.Dispose();
            _cleanupError ??= failure;
            entry.Drained.SetResult(failure);
            CompleteCloseIfDrained();
        }
    }

    private void RemoveIndex(Entry entry)
    {
        if (_entries.TryGetValue(entry.Key, out var indexed) && ReferenceEquals(entry, indexed))
            _entries.Remove(entry.Key);
    }

    private void CompleteCloseIfDrained()
    {
        if (_close is not { } close || _owned.Count != 0) return;
        if (_cleanupError is { } error) close.SetException(error);
        else close.SetResult();
    }

    internal sealed class Entry(TKey key)
    {
        internal readonly TKey Key = key;
        internal readonly CancellationTokenSource Stop = new();
        internal readonly TaskCompletionSource<TValue> Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<Exception?> Drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task? Cancellation;
        internal int References;
    }

    public sealed class Lease : IAsyncDisposable
    {
        private AssetLeasePool<TKey, TValue>? _pool;
        private Entry? _entry;
        private TValue? _value;
        private Task? _release;

        internal Lease(AssetLeasePool<TKey, TValue> pool, Entry entry, TValue value)
            => (_pool, _entry, _value) = (pool, entry, value);

        public TValue Value => Volatile.Read(ref _value) ?? throw new ObjectDisposedException(nameof(Lease));

        /// <summary>Release the lease. Repeated calls observe the same completion and cleanup failure.</summary>
        public ValueTask DisposeAsync()
        {
            var pool = Volatile.Read(ref _pool);
            if (pool is null) return _release is { } released ? new(released) : default;
            Entry entry;
            Task? cancellation;
            lock (pool._gate) {
                if (_entry is not { } current) return _release is { } released ? new(released) : default;
                entry = current;
                Volatile.Write(ref _value, null);
                cancellation = BeginRelease(entry);
                _release = cancellation is null ? null : ObserveReleaseAsync(entry.Drained.Task);
                _entry = null;
                Volatile.Write(ref _pool, null);
            }
            if (cancellation is not null) _ = pool.DrainAsync(entry, cancellation);
            return _release is { } release ? new(release) : default;
        }

        private static async Task ObserveReleaseAsync(Task<Exception?> drain)
        {
            if (await drain.ConfigureAwait(false) is { } error) ExceptionDispatchInfo.Throw(error);
        }
    }
}
