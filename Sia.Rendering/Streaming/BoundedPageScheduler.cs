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
    private readonly BoundedRequests<string, StreamGeometryPage> _requests;
    private readonly Dictionary<string, DateTime> _retry = [with(StringComparer.Ordinal)];
    private readonly Dictionary<string, StreamPageDemand> _wanted = [with(StringComparer.Ordinal)];
    private readonly List<string> _expiredRetries = [];
    private readonly List<BoundedRequests<string, StreamGeometryPage>.Request> _requestSnapshot = [];
    private readonly long _maximumBytes;
    private long _completed, _failed, _cancelled;
    private bool _disposed;
    private Task? _stop;

    public PageSchedulerStatistics Statistics => new(_requests.Count, _requests.ReservedBytes, _requests.PeakBytes, _completed, _failed, _cancelled);

    public IEnumerable<string> ReadyIds => _requests.Requests.Where(p => !p.IsCancellationRequested
        && p.Read.IsCompletedSuccessfully).Select(p => p.Key);

    public BoundedPageScheduler(Func<string, CancellationToken, Task<StreamGeometryPage>> read,
        int maximumRequests = 2, long maximumDecodedBytes = 2 * StreamGeometryPage.MaximumBytes)
    {
        ArgumentNullException.ThrowIfNull(read);
        if (maximumRequests < 1 || maximumDecodedBytes < StreamGeometryPage.MaximumBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumRequests));
        _maximumBytes = maximumDecodedBytes;
        _requests = new((id, token) => Task.Run(() => read(id, token)), maximumRequests, maximumDecodedBytes, StringComparer.Ordinal);
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
        foreach (var request in _requests.Requests)
            if (wanted.TryGetValue(request.Key, out var demand) && request.ReservedBytes != demand.DecodedBytes)
                throw new ArgumentException("An in-flight page reservation cannot change size.");
        _requestSnapshot.Clear();
        _requestSnapshot.AddRange(_requests.Requests);
        foreach (var request in _requestSnapshot) {
            if (!wanted.ContainsKey(request.Key) && _requests.Cancel(request.Key)) _cancelled++;
            if (request.Read.IsCompleted && (request.IsCancellationRequested || !request.Read.IsCompletedSuccessfully)) {
                if (request.Read.IsFaulted) {
                    _ = request.Read.Exception;
                    _failed++;
                    _retry[request.Key] = DateTime.UtcNow.AddSeconds(1);
                    if (_retry.Count > 1024) _retry.Remove(_retry.MinBy(p => p.Value).Key);
                }
                _requests.Release(request.Key);
            }
        }
        _requestSnapshot.Clear();
        Admit();
    }

    private void Admit()
    {
        while (_requests.Count < _requests.Capacity) {
            StreamPageDemand? best = null;
            foreach (var candidate in _wanted.Values) {
                if (_requests.Contains(candidate.Id) || candidate.DecodedBytes > _maximumBytes - _requests.ReservedBytes
                    || _retry.ContainsKey(candidate.Id))
                    continue;
                if (best is not { } current || candidate.Priority > current.Priority
                    || (candidate.Priority == current.Priority && StringComparer.Ordinal.Compare(candidate.Id, current.Id) < 0))
                    best = candidate;
            }
            if (best is not { } d) break;
            if (!_requests.TryStart(d.Id, d.DecodedBytes)) break;
        }
    }

    public bool TryGetReady(string id, out StreamGeometryPage? page)
    {
        page = null;
        if (!_requests.TryGet(id, out var request) || request.IsCancellationRequested || !request.Read.IsCompletedSuccessfully)
            return false;
        page = request.Read.Result;
        if (page.Bytes.Length != request.ReservedBytes)
            throw new InvalidDataException("Decoded page exceeded its reservation.");
        return true;
    }

    /// <summary>Releases a fully consumed page and immediately admits queued reads within both limits.</summary>
    public void Acknowledge(string id)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_requests.TryGet(id, out var request) || !request.Read.IsCompletedSuccessfully || request.IsCancellationRequested)
            throw new InvalidOperationException("Page request is not successfully ready.");
        _requests.Release(id);
        _completed++;
        _wanted.Remove(id);
        Admit();
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        return new(_stop ??= DrainAsync());
    }

    private async Task DrainAsync()
    {
        await _requests.DisposeAsync().ConfigureAwait(false);
        _retry.Clear();
        _wanted.Clear();
        _expiredRetries.Clear();
        _requestSnapshot.Clear();
    }
}
