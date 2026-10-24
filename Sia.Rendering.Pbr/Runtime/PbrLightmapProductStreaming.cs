using ProductReference = Sia.Asset.AssetRefer<Sia.Engine.Rendering.Pbr.PbrLightmapRecord>.Id;

namespace Sia.Engine.Rendering.Pbr;

internal readonly record struct PbrLightmapProductStatistics(int Resident, int Retiring, ulong CpuBytes,
    uint UploadedBytes, int DeferredProducts, PageSchedulerStatistics Requests);

// One frame union and bounded typed member preparation. World acquisition is
// invoked by the pool only on the frame owner, after admission; IO never mutates World.
internal sealed class PbrLightmapProductStreaming : IDisposable
{
    private readonly PbrLightmapSetData _set;
    private readonly PbrLightmapProductPool _pool;
    private readonly Func<ProductReference, PbrLightmapRecord, (PbrLightmapData Data, Action Release)> _use;
    private readonly BoundedPageScheduler<PbrLightmapRecord> _requests;
    private readonly string[] _ids;
    private readonly Dictionary<string, int> _indices = new(StringComparer.Ordinal);
    private readonly int[] _bytes;
    private readonly bool[] _eligible, _evicting;
    private readonly long[] _lastUsed;
    private readonly Dictionary<int, float> _wanted = [];
    private readonly List<StreamPageDemand> _demands = [];
    private readonly List<int> _ready = [];
    private readonly uint _uploadBytesPerFrame;
    private readonly int _idleFrames;
    private long _clock;
    private uint _uploaded;
    private bool _inFrame, _stopping, _stopped;
    private Exception? _failure;
    private Task? _stop;
    internal bool IsStopped => _stopped;
    internal PbrLightmapProductStatistics Statistics => new(_pool.ResidentProducts, _pool.RetiringProducts,
        _pool.CpuBytes, _uploaded, _wanted.Keys.Count(product => !_eligible[product]), _requests.Statistics);

    internal PbrLightmapProductStreaming(PbrLightmapSetData set, PbrLightmapProductPool pool,
        uint uploadBytesPerFrame, int idleFrames, int maximumRequests, long decodedBytes,
        Func<ProductReference, CancellationToken, Task<PbrLightmapRecord>> read,
        Func<ProductReference, PbrLightmapRecord, (PbrLightmapData Data, Action Release)> use)
    {
        ArgumentNullException.ThrowIfNull(set); ArgumentNullException.ThrowIfNull(pool);
        ArgumentNullException.ThrowIfNull(read); ArgumentNullException.ThrowIfNull(use);
        if (!pool.Matches(set)) throw new ArgumentException("Product pool belongs to another lighting set revision.", nameof(pool));
        if (uploadBytesPerFrame < 16 || idleFrames < 1 || maximumRequests < 1 || decodedBytes < 1)
            throw new ArgumentOutOfRangeException(nameof(uploadBytesPerFrame));
        _set = set; _pool = pool; _use = use; _uploadBytesPerFrame = uploadBytesPerFrame; _idleFrames = idleFrames;
        var count = set.Products.Length; _ids = new string[count]; _bytes = new int[count]; _eligible = new bool[count];
        _evicting = new bool[count]; _lastUsed = new long[count]; var maximumBytes = 1;
        for (var at = 0; at < count; at++) {
            var shape = set.ProductCounts(at); _bytes[at] = checked((int)PbrLightmapData.RequiredBytes(shape.Receivers, shape.Charts, shape.Pages, set.BakeSettings));
            _ids[at] = set.Products[at].Id.Value.ToString("N"); _indices.Add(_ids[at], at);
            _eligible[at] = _bytes[at] <= decodedBytes && (ulong)shape.Receivers * 16 <= uploadBytesPerFrame && pool.CanFit(at);
            if (_eligible[at]) maximumBytes = System.Math.Max(maximumBytes, _bytes[at]);
        }
        _requests = new(async (id, token) => {
            var product = _indices[id]; var record = await read(_set.Products[product].Reference, token).ConfigureAwait(false);
            _set.ValidateProduct(product, record.Data, token); token.ThrowIfCancellationRequested(); return record;
        }, record => checked((int)record.Data.PayloadBytes), maximumBytes, maximumRequests, decodedBytes);
    }

    internal void BeginFrame()
    {
        RequireRunning(); if (_inFrame) throw new InvalidOperationException("Product frame is already open.");
        _pool.Reclaim(); _clock = checked(_clock + 1); _uploaded = 0; _wanted.Clear(); _inFrame = true;
    }

    internal void Demand(int product, float priority)
    {
        RequireRunning();
        if (!_inFrame) throw new InvalidOperationException("Begin the product frame before demand.");
        if ((uint)product >= _ids.Length || !float.IsFinite(priority) || priority < 0) throw new ArgumentException("Invalid product demand.");
        if (!_wanted.TryGetValue(product, out var old) || priority > old) _wanted[product] = priority;
        _lastUsed[product] = _clock;
    }

    internal void EndFrame()
    {
        RequireRunning(); if (!_inFrame) throw new InvalidOperationException("No product frame is open."); _inFrame = false;
        var completed = _requests.Statistics.Completed;
        try {
            Update();
            // Ready results occupy request slots until owner admission acknowledges them.
            if (_requests.Statistics.Completed != completed) UpdateRequests();
        }
        catch (Exception error) { _failure = error; throw; }
    }

    private void Update()
    {
        for (var at = 0; at < _ids.Length; at++) if (_evicting[at]) Evict(at);
        if (_pool.ActiveProduct is >= 0 and var active && !_wanted.ContainsKey(active)) Evict(active);
        for (var at = 0; at < _ids.Length; at++)
            if (_pool.Resident(at) is not null && !_wanted.ContainsKey(at) && _clock - _lastUsed[at] >= _idleFrames) Evict(at);
        UpdateRequests();
        _uploaded += _pool.Advance(_uploadBytesPerFrame - _uploaded);
        if (_pool.ActiveProduct >= 0) return;
        _ready.Clear();
        foreach (var id in _requests.ReadyIds) {
            var product = _indices[id]; if (_wanted.ContainsKey(product)) _ready.Add(product);
        }
        _ready.Sort((a, b) => { var order = _wanted[b].CompareTo(_wanted[a]); return order == 0 ? a.CompareTo(b) : order; });
        foreach (var product in _ready) {
            if (_uploadBytesPerFrame - _uploaded < 16 || !_requests.TryGetReady(_ids[product], out var record)) break;
            while (!_pool.BeginUpload(product, record!.Data, () => _use(_set.Products[product].Reference, record))) {
                var victim = Victim(product); if (victim < 0) return;
                Evict(victim); _pool.Reclaim();
                if (_uploadBytesPerFrame - _uploaded < 16) return;
            }
            _requests.Acknowledge(_ids[product]);
            _uploaded += _pool.Advance(_uploadBytesPerFrame - _uploaded);
            if (_pool.ActiveProduct >= 0) return;
        }
    }

    private void UpdateRequests()
    {
        _demands.Clear();
        foreach (var (product, priority) in _wanted)
            if (_eligible[product] && !_pool.HasAllocation(product) && !_evicting[product]) _demands.Add(new(_ids[product], _bytes[product], priority));
        _requests.Update(_demands);
    }

    private int Victim(int desired)
    {
        var victim = -1;
        for (var at = 0; at < _ids.Length; at++) {
            if (_pool.Resident(at) is null || _evicting[at]) continue;
            var priority = _wanted.GetValueOrDefault(at, -1);
            if (priority > _wanted[desired] || priority == _wanted[desired] && at < desired) continue;
            if (victim < 0 || priority < _wanted.GetValueOrDefault(victim, -1)
                || priority == _wanted.GetValueOrDefault(victim, -1) && at > victim) victim = at;
        }
        return victim;
    }

    private void Evict(int product)
    {
        _evicting[product] = !_pool.Evict(product, _uploadBytesPerFrame - _uploaded, out var bytes); _uploaded += bytes;
    }

    internal Task StopAsync()
    {
        if (_stop is not null) return _stop;
        _stopping = true; _inFrame = false;
        // Capture on owner context before joining a read that may resume elsewhere.
        return _stop = DrainAsync(_pool.StopAsync(), _requests.DisposeAsync().AsTask());
    }

    private async Task DrainAsync(Task queue, Task reads)
    {
        await Task.WhenAll(queue, reads).ConfigureAwait(false); _stopped = true;
    }

    public void Dispose()
    {
        if (!_stopped) throw new InvalidOperationException("Stop product IO and queue work before owner-side disposal.");
        _pool.Dispose();
    }

    private void RequireRunning()
    {
        ObjectDisposedException.ThrowIf(_stopping, this);
        if (_failure is not null) throw new InvalidOperationException("Product frame effects failed.", _failure);
    }
}
