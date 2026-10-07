namespace Sia.Engine.Rendering;

/// <summary>
/// Owns a bounded set of typed asynchronous reads. The caller supplies admission order,
/// retries and publication; canceled reads retain their reservations until terminal.
/// Coordinate this owner from one thread; read tasks may complete on any thread.
/// </summary>
public sealed class BoundedRequests<TKey, TValue> : IAsyncDisposable where TKey : notnull
{
    public sealed class Request
    {
        internal readonly CancellationTokenSource Cancellation = new();
        public TKey Key { get; }
        public long ReservedBytes { get; }
        public Task<TValue> Read { get; internal set; } = null!;
        public bool IsCancellationRequested => Cancellation.IsCancellationRequested;

        internal Request(TKey key, long bytes) => (Key, ReservedBytes) = (key, bytes);
    }

    private readonly Func<TKey, CancellationToken, Task<TValue>> _read;
    private readonly List<Request> _ordered = [];
    private readonly Dictionary<TKey, Request> _requests;
    private Task? _stop;
    private bool _stopping;

    public int Capacity { get; }
    public long MaximumBytes { get; }
    public long ReservedBytes { get; private set; }
    public long PeakBytes { get; private set; }
    public int Count => _ordered.Count;
    /// <summary>Live admission order. Snapshot it before releasing requests during enumeration.</summary>
    public IReadOnlyList<Request> Requests => _ordered;

    public BoundedRequests(Func<TKey, CancellationToken, Task<TValue>> read, int capacity,
        long maximumBytes, IEqualityComparer<TKey>? comparer = null)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumBytes, 1);
        (_read, Capacity, MaximumBytes) = (read, capacity, maximumBytes);
        _requests = new(comparer);
    }

    public bool Contains(TKey key) => _requests.ContainsKey(key);
    public bool TryGet(TKey key, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Request? request) =>
        _requests.TryGetValue(key, out request);

    public bool TryStart(TKey key, long bytes)
    {
        ObjectDisposedException.ThrowIf(_stopping, this);
        ArgumentOutOfRangeException.ThrowIfLessThan(bytes, 1);
        if (_requests.ContainsKey(key) || Count == Capacity || bytes > MaximumBytes - ReservedBytes) return false;
        var request = new Request(key, bytes);
        _requests.Add(key, request);
        _ordered.Add(request);
        ReservedBytes += bytes;
        PeakBytes = System.Math.Max(PeakBytes, ReservedBytes);
        try {
            request.Read = _read(key, request.Cancellation.Token)
                ?? Task.FromException<TValue>(new InvalidOperationException("A request reader returned no task."));
        }
        catch (Exception error) { request.Read = Task.FromException<TValue>(error); }
        return true;
    }

    public bool Cancel(TKey key)
    {
        ObjectDisposedException.ThrowIf(_stopping, this);
        if (!_requests.TryGetValue(key, out var request) || request.IsCancellationRequested) return false;
        request.Cancellation.Cancel();
        return true;
    }

    public void Release(TKey key)
    {
        ObjectDisposedException.ThrowIf(_stopping, this);
        var request = _requests[key];
        if (!request.Read.IsCompleted) throw new InvalidOperationException("A running read still owns its reservation.");
        _ = request.Read.Exception;
        request.Cancellation.Dispose();
        _ordered.Remove(request);
        _requests.Remove(key);
        ReservedBytes -= request.ReservedBytes;
    }

    public ValueTask DisposeAsync()
    {
        _stopping = true;
        return new(_stop ??= DrainAsync());
    }

    private async Task DrainAsync()
    {
        foreach (var request in _ordered) request.Cancellation.Cancel();
        foreach (var request in _ordered) {
            try { await request.Read.ConfigureAwait(false); }
            catch (Exception) { _ = request.Read.Exception; }
            request.Cancellation.Dispose();
        }
        _ordered.Clear();
        _requests.Clear();
        ReservedBytes = 0;
    }
}
