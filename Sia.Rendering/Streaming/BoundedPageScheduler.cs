using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Diagnostics;

namespace Sia.Engine.Rendering;

public readonly record struct StreamPageDemand(string Id, int DecodedBytes, float Priority);
public readonly record struct PageSchedulerStatistics(
    int Pending,
    long ReservedBytes,
    long PeakBytes,
    long Completed,
    long Failed,
    long Cancelled)
{
    public int Preparing { get; init; }
    public int Ready { get; init; }
    public int Installing { get; init; }
    public int Cancelling { get; init; }
    public int MaximumPreparations { get; init; }
    public int MaximumBufferedRequests { get; init; }
    public long Started { get; init; }
    public long Prepared { get; init; }
    public long InstallStarted { get; init; }
    public long Acknowledged { get; init; }
    // Successful preparation and closed ready/install stage wall dwell. These
    // are not CPU or network durations; ongoing stages are counted above.
    public double PreparationMilliseconds { get; init; }
    public double ReadyMilliseconds { get; init; }
    public double InstallingMilliseconds { get; init; }
}

public sealed class BoundedPageScheduler<TPrepared> : IAsyncDisposable
{
    private sealed class Request(StreamPageDemand demand)
    {
        public readonly StreamPageDemand Demand = demand;
        public readonly CancellationTokenSource Cancellation = new();
        public Task<TPrepared> Task = null!;
        public readonly long Started = Stopwatch.GetTimestamp();
        public long Finished;
        public long InstallStarted;
        public bool PreparedObserved;
        public bool FailureObserved;
        public bool DwellClosed;
    }

    private readonly Func<string, CancellationToken, Task<TPrepared>> _read;
    private readonly Dictionary<string, Request> _requests = [with(StringComparer.Ordinal)];
    private readonly Dictionary<string, DateTime> _retry = [with(StringComparer.Ordinal)];
    private readonly Dictionary<string, StreamPageDemand> _wanted = [with(StringComparer.Ordinal)];
    private readonly List<string> _expiredRetries = [];
    private readonly List<KeyValuePair<string, Request>> _requestSnapshot = [];
    private readonly Func<TPrepared, int> _decodedBytes;
    private readonly int _maximumPageBytes;
    private readonly int _maximumRequests;
    private readonly int _maximumBufferedRequests;
    private readonly long _maximumBytes;
    private long _reserved, _peak, _completed, _failed, _cancelled;
    private long _started, _preparedCount, _installStarted;
    private double _preparationMilliseconds, _readyMilliseconds, _installingMilliseconds;
    private bool _disposed;
    private TaskCompletionSource? _disposeCompletion;

    public PageSchedulerStatistics Statistics {
        get {
            var preparing = 0; var ready = 0; var installing = 0; var cancelling = 0;
            foreach (var request in _requests.Values) {
                if (!request.Task.IsCompleted) {
                    preparing++;
                    if (request.Cancellation.IsCancellationRequested) cancelling++;
                }
                else if (!request.Cancellation.IsCancellationRequested && request.Task.IsCompletedSuccessfully) {
                    if (request.InstallStarted != 0) installing++; else ready++;
                }
            }
            return new(_requests.Count, _reserved, _peak, _completed, _failed, _cancelled) {
                Preparing = preparing, Ready = ready, Installing = installing, Cancelling = cancelling,
                MaximumPreparations = _maximumRequests, MaximumBufferedRequests = _maximumBufferedRequests,
                Started = _started, Prepared = _preparedCount, InstallStarted = _installStarted, Acknowledged = _completed,
                PreparationMilliseconds = _preparationMilliseconds, ReadyMilliseconds = _readyMilliseconds,
                InstallingMilliseconds = _installingMilliseconds
            };
        }
    }

    /// <summary>Prepared records, including records already being installed, retained until acknowledgement.</summary>
    public IEnumerable<string> ReadyIds => _requests.Where(p => !p.Value.Cancellation.IsCancellationRequested
        && p.Value.Task.IsCompletedSuccessfully).Select(p => p.Key);

    /// <summary>Owner-context query including canceled reads that still retain their reservation.</summary>
    public bool HasRequest(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return _requests.ContainsKey(id);
    }

    public BoundedPageScheduler(Func<string, CancellationToken, Task<TPrepared>> read, Func<TPrepared, int> decodedBytes, int maximumPageBytes,
        int maximumRequests = 2, long maximumDecodedBytes = 1024 * 1024)
    {
        ArgumentNullException.ThrowIfNull(read);
        if (maximumRequests < 1 || maximumPageBytes < 1 || maximumDecodedBytes < maximumPageBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumRequests));
        ArgumentNullException.ThrowIfNull(decodedBytes);
        (_read, _decodedBytes, _maximumPageBytes, _maximumRequests, _maximumBytes) = (read, decodedBytes, maximumPageBytes, maximumRequests, maximumDecodedBytes);
        // Ready/installing data retains its byte reservation, but does not consume
        // a preparation slot. A second, finite item cap also bounds tiny pages.
        var readyCapacity = System.Math.Min((long)maximumRequests, maximumDecodedBytes / maximumPageBytes);
        _maximumBufferedRequests = (int)System.Math.Min(int.MaxValue, maximumRequests + readyCapacity);
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
            if (string.IsNullOrEmpty(d.Id) || (d.DecodedBytes < 1 || d.DecodedBytes > _maximumPageBytes) || !float.IsFinite(d.Priority))
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
                _cancelled++;
                request.Cancellation.Cancel();
            }
            if (request.Task.IsCompleted && (request.Cancellation.IsCancellationRequested || !request.Task.IsCompletedSuccessfully)) {
                ObserveFailure(request);
                if (request.Task.IsFaulted) {
                    _retry[pair.Key] = DateTime.UtcNow.AddSeconds(1);
                    if (_retry.Count > 1024) _retry.Remove(_retry.MinBy(p => p.Value).Key);
                }
                Remove(pair.Key);
            }
            else if (request.Task.IsCompletedSuccessfully) ObservePrepared(request);
        }
        _requestSnapshot.Clear();
        // Completed tasks can make room during this same Update. The buffered
        // item cap and decoded reservation make this finite even for sync reads.
        while (_requests.Count < _maximumBufferedRequests && PreparingCount() < _maximumRequests) {
            StreamPageDemand? best = null;
            foreach (var candidate in wanted.Values) {
                if (_requests.ContainsKey(candidate.Id) || candidate.DecodedBytes > _maximumBytes - _reserved
                    || _retry.ContainsKey(candidate.Id))
                    continue;
                if (best is not { } current || candidate.Priority > current.Priority
                    || (candidate.Priority == current.Priority && StringComparer.Ordinal.Compare(candidate.Id, current.Id) < 0))
                    best = candidate;
            }
            if (best is not { } d) break;
            var request = new Request(d);
            _requests.Add(d.Id, request);
            _reserved += d.DecodedBytes;
            _peak = System.Math.Max(_peak, _reserved);
            _started++;
            try {
                request.Task = Task.Run(() => PrepareAsync(request));
            }
            catch (Exception error) {
                Volatile.Write(ref request.Finished, Stopwatch.GetTimestamp());
                request.Task = Task.FromException<TPrepared>(error);
            }
        }
    }

    public bool TryGetReady(string id, out TPrepared? page)
    {
        page = default;
        if (!_requests.TryGetValue(id, out var request) || request.Cancellation.IsCancellationRequested || !request.Task.IsCompletedSuccessfully)
            return false;
        ObservePrepared(request);
        page = request.Task.Result;
        if (_decodedBytes(page!) != request.Demand.DecodedBytes)
            throw new InvalidDataException("Decoded page exceeded its reservation.");
        return true;
    }

    /// <summary>Marks the owner beginning upload/installation. The decoded reservation survives until acknowledgement.</summary>
    public void BeginInstall(string id)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_requests.TryGetValue(id, out var request) || !request.Task.IsCompletedSuccessfully || request.Cancellation.IsCancellationRequested)
            throw new InvalidOperationException("Installation requires a successfully prepared wanted page.");
        ObservePrepared(request);
        if (request.InstallStarted != 0) return;
        request.InstallStarted = Stopwatch.GetTimestamp();
        _installStarted++;
        _readyMilliseconds += Milliseconds(Finished(request), request.InstallStarted);
    }

    public void Acknowledge(string id)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_requests.TryGetValue(id, out var request) || !request.Task.IsCompletedSuccessfully || request.Cancellation.IsCancellationRequested)
            throw new InvalidOperationException("Page request is not successfully ready.");
        ObservePrepared(request);
        CloseDwell(request);
        _completed++;
        Remove(id);
    }

    public ValueTask DisposeAsync()
    {
        if (_disposeCompletion is { } existing) return new(existing.Task);
        _disposeCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _disposed = true;
        _ = DisposeCoreAsync(_disposeCompletion);
        return new(_disposeCompletion.Task);
    }

    private async Task DisposeCoreAsync(TaskCompletionSource completion)
    {
        List<Exception>? errors = null;
        foreach (var request in _requests.Values) {
            if (request.Cancellation.IsCancellationRequested) continue;
            _cancelled++;
            try { request.Cancellation.Cancel(); }
            catch (Exception error) { (errors ??= []).Add(error); }
        }
        foreach (var request in _requests.Values) {
            try {
                await request.Task;
            }
            catch (Exception) { ObserveFailure(request); }
            CloseDwell(request);
            try { request.Cancellation.Dispose(); }
            catch (Exception error) { (errors ??= []).Add(error); }
        }
        _requests.Clear();
        _retry.Clear();
        _wanted.Clear();
        _expiredRetries.Clear();
        _requestSnapshot.Clear();
        _reserved = 0;
        if (errors is null) completion.SetResult();
        else completion.SetException(errors.Count == 1 ? errors[0] : new AggregateException(errors));
    }

    private async Task<TPrepared> PrepareAsync(Request request)
    {
        try { return await _read(request.Demand.Id, request.Cancellation.Token).ConfigureAwait(false); }
        finally { Volatile.Write(ref request.Finished, Stopwatch.GetTimestamp()); }
    }

    private int PreparingCount()
    {
        var count = 0;
        foreach (var request in _requests.Values) if (!request.Task.IsCompleted) count++;
        return count;
    }

    private static long Finished(Request request)
    {
        var finished = Volatile.Read(ref request.Finished);
        return finished == 0 ? Stopwatch.GetTimestamp() : finished;
    }

    private static double Milliseconds(long start, long end) => (end - start) * 1000d / Stopwatch.Frequency;

    private void ObservePrepared(Request request)
    {
        if (request.PreparedObserved) return;
        if (_decodedBytes(request.Task.Result) != request.Demand.DecodedBytes)
            throw new InvalidDataException("Decoded page exceeded its reservation.");
        request.PreparedObserved = true;
        _preparedCount++;
        _preparationMilliseconds += Milliseconds(request.Started, Finished(request));
    }

    private void ObserveFailure(Request request)
    {
        if (request.FailureObserved || !request.Task.IsFaulted) return;
        _ = request.Task.Exception;
        request.FailureObserved = true;
        _failed++;
    }

    private void CloseDwell(Request request)
    {
        if (request.DwellClosed || !request.Task.IsCompletedSuccessfully) return;
        var now = Stopwatch.GetTimestamp();
        if (request.InstallStarted == 0) _readyMilliseconds += Milliseconds(Finished(request), now);
        else _installingMilliseconds += Milliseconds(request.InstallStarted, now);
        request.DwellClosed = true;
    }

    private void Remove(string id)
    {
        var request = _requests[id];
        CloseDwell(request);
        _reserved -= request.Demand.DecodedBytes;
        request.Cancellation.Dispose();
        _requests.Remove(id);
    }
}
