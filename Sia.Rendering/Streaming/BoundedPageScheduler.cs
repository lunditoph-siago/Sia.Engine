using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;

namespace Sia.Engine.Rendering;

public readonly record struct StreamPageDemand(string Id, int DecodedBytes, float Priority);
public readonly record struct PageSchedulerStatistics(
    int Pending,
    long ReservedBytes,
    long PeakBytes,
    long Completed,
    long Failed,
    long Cancelled);

public sealed class BoundedPageScheduler : IAsyncDisposable
{
    private sealed class Request(
        StreamPageDemand demand,
        Task<StreamGeometryPage> task,
        CancellationTokenSource cancellation)
    {
        public readonly StreamPageDemand Demand = demand;
        public readonly CancellationTokenSource Cancellation = cancellation;
        public readonly Task<StreamGeometryPage> Task = task;
    }

    private readonly Func<string, CancellationToken, Task<StreamGeometryPage>> _read;
    private readonly Dictionary<string, Request> _requests = [with(StringComparer.Ordinal)];
    private readonly Dictionary<string, DateTime> _retry = [with(StringComparer.Ordinal)];
    private readonly Dictionary<string, StreamPageDemand> _wanted = [with(StringComparer.Ordinal)];
    private readonly List<string> _expiredRetries = [];
    private readonly List<KeyValuePair<string, Request>> _requestSnapshot = [];
    private readonly int _maximumRequests;
    private readonly long _maximumBytes;
    private long _reserved, _peak, _completed, _failed, _cancelled;
    private bool _disposed;

    public PageSchedulerStatistics Statistics => new(_requests.Count, _reserved, _peak, _completed, _failed, _cancelled);

    public IEnumerable<string> ReadyIds => _requests.Where(p => !p.Value.Cancellation.IsCancellationRequested
        && p.Value.Task.IsCompletedSuccessfully).Select(p => p.Key);

    public BoundedPageScheduler(Func<string, CancellationToken, Task<StreamGeometryPage>> read,
        int maximumRequests = 2, long maximumDecodedBytes = 2 * StreamGeometryPage.MaximumBytes)
    {
        ArgumentNullException.ThrowIfNull(read);
        if (maximumRequests < 1 || maximumDecodedBytes < StreamGeometryPage.MaximumBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumRequests));
        (_read, _maximumRequests, _maximumBytes) = (read, maximumRequests, maximumDecodedBytes);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Update(IEnumerable<StreamPageDemand> demands)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var wanted = _wanted;
        wanted.Clear();
        _expiredRetries.Clear();
        var now = DateTime.UtcNow;
        foreach (var pair in _retry)
            if (pair.Value <= now) _expiredRetries.Add(pair.Key);
        foreach (var id in _expiredRetries)
            _retry.Remove(id);
        foreach (var d in demands) {
            if (string.IsNullOrEmpty(d.Id) || d.DecodedBytes is < 28 or > StreamGeometryPage.MaximumBytes || !float.IsFinite(d.Priority))
                throw new ArgumentException("Invalid page demand.");
            ref var previous = ref CollectionsMarshal.GetValueRefOrAddDefault(wanted, d.Id, out var exists);
            if (exists) {
                if (previous.DecodedBytes != d.DecodedBytes)
                    throw new ArgumentException("Conflicting reservations for one page identity.");
                if (d.Priority > previous.Priority) previous = d;
            }
            else previous = d;
        }
        foreach (var pair in _requests)
            if (wanted.TryGetValue(pair.Key, out var demand) && pair.Value.Demand.DecodedBytes != demand.DecodedBytes)
                throw new ArgumentException("An in-flight page reservation cannot change size.");
        _requestSnapshot.Clear();
        foreach (var pair in _requests)
            _requestSnapshot.Add(pair);
        foreach (var pair in _requestSnapshot) {
            var request = pair.Value;
            if (!wanted.ContainsKey(pair.Key) && !request.Cancellation.IsCancellationRequested) {
                request.Cancellation.Cancel();
                _cancelled++;
            }
            if (request.Task.IsCompleted && (request.Cancellation.IsCancellationRequested || !request.Task.IsCompletedSuccessfully)) {
                if (request.Task.IsFaulted) {
                    _ = request.Task.Exception;
                    _failed++;
                    _retry[pair.Key] = DateTime.UtcNow.AddSeconds(1);
                    if (_retry.Count > 1024) _retry.Remove(_retry.MinBy(p => p.Value).Key);
                }
                Remove(pair.Key);
            }
        }
        _requestSnapshot.Clear();
        while (_requests.Count < _maximumRequests) {
            StreamPageDemand? best = null;
            foreach (var candidate in wanted.Values) {
                if (_requests.ContainsKey(candidate.Id) || _reserved + candidate.DecodedBytes > _maximumBytes
                    || _retry.ContainsKey(candidate.Id))
                    continue;
                if (best is not { } current || candidate.Priority > current.Priority
                    || (candidate.Priority == current.Priority && StringComparer.Ordinal.Compare(candidate.Id, current.Id) < 0))
                    best = candidate;
            }
            if (best is not { } d) break;
            var cancellation = new CancellationTokenSource();
            Task<StreamGeometryPage> task;
            try {
                task = Task.Run(() => _read(d.Id, cancellation.Token));
            }
            catch (Exception error) { task = Task.FromException<StreamGeometryPage>(error); }
            var request = new Request(d, task, cancellation);
            _requests.Add(d.Id, request);
            _reserved += d.DecodedBytes;
            _peak = System.Math.Max(_peak, _reserved);
        }
    }

    public bool TryGetReady(string id, out StreamGeometryPage? page)
    {
        page = null;
        if (!_requests.TryGetValue(id, out var request) || request.Cancellation.IsCancellationRequested || !request.Task.IsCompletedSuccessfully)
            return false;
        page = request.Task.Result;
        if (page.Bytes.Length != request.Demand.DecodedBytes)
            throw new InvalidDataException("Decoded page exceeded its reservation.");
        return true;
    }

    public void Acknowledge(string id)
    {
        if (!_requests.TryGetValue(id, out var request) || !request.Task.IsCompletedSuccessfully || request.Cancellation.IsCancellationRequested)
            throw new InvalidOperationException("Page request is not successfully ready.");
        _completed++;
        Remove(id);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var request in _requests.Values)
            request.Cancellation.Cancel();
        foreach (var request in _requests.Values) {
            try {
                await request.Task.ConfigureAwait(false);
            }
            catch (Exception) { _ = request.Task.Exception; }
            request.Cancellation.Dispose();
        }
        _requests.Clear();
        _retry.Clear();
        _wanted.Clear();
        _expiredRetries.Clear();
        _requestSnapshot.Clear();
        _reserved = 0;
    }

    private void Remove(string id)
    {
        var request = _requests[id];
        _reserved -= request.Demand.DecodedBytes;
        request.Cancellation.Dispose();
        _requests.Remove(id);
    }
}
