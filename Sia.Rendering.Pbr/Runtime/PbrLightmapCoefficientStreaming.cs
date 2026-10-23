using System.Runtime.CompilerServices;
using Sia.Math;

namespace Sia.Engine.Rendering.Pbr;

// Child requests bind one immutable member and one leased address generation.
// Parent withdrawal joins their terminal reads before retiring metadata/asset ownership.
internal sealed class PbrLightmapCoefficientStreaming
{
    private sealed record Context(int Product, PbrLightmapData Member, StreamRange Pages, int Page, string Key)
    {
        internal int Address => checked((int)Pages.Offset + Page);
        internal PbrLightmapPageDescriptor Descriptor => Member.Pages[Page];
    }
    private sealed record Prepared(Context Context, PbrLightmapPageRecord Record);
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Context> _contexts = new(StringComparer.Ordinal);
    private readonly Dictionary<int, Context> _addresses = [];
    private readonly Dictionary<string, float> _wanted = new(StringComparer.Ordinal);
    private readonly List<StreamPageDemand> _demands = [];
    private readonly List<string> _ready = [], _removed = [];
    private readonly long[] _lastUsed;
    private readonly PbrLightmapProductPool _products;
    private readonly Func<PbrLightmapData, int, PbrLightmapPageRecord, PbrLightmapPageData> _use;
    private readonly BoundedPageScheduler<Prepared> _requests;
    private readonly uint _uploadBudget;
    private readonly int _idleFrames;
    private long _clock;
    private uint _uploaded;
    private bool _inFrame, _stopping, _stopped;
    private Task? _stop;
    private Exception? _failure;
    internal PbrLightmapPagePool Pool { get; }
    internal PageSchedulerStatistics Requests => _requests.Statistics;
    internal uint UploadedBytes => _uploaded;
    internal bool IsStopped => _stopped;

    internal static int DecodedBytes(PbrLightmapPageDescriptor page)
        => checked(4 * Unsafe.SizeOf<float3>() + page.Edge * page.Edge * 25);

    internal PbrLightmapCoefficientStreaming(PbrLightmapProductPool products, int addresses, int slots,
        uint uploadBytesPerFrame, int idleFrames, int maximumRequests, long decodedBytes,
        Func<PbrLightmapData, int, CancellationToken, Task<PbrLightmapPageRecord>> read,
        Func<PbrLightmapData, int, PbrLightmapPageRecord, PbrLightmapPageData> use,
        PbrLightmapPageWrite write, Action<int, int> map, Func<Task<bool>> afterSubmittedWork)
    {
        ArgumentNullException.ThrowIfNull(products); ArgumentNullException.ThrowIfNull(read); ArgumentNullException.ThrowIfNull(use);
        if (addresses is < 1 or > PbrLightmapData.MaximumPages || (uint)addresses != products.PageAddressCapacity || slots < 1 || slots > addresses
            || uploadBytesPerFrame < 32 || idleFrames < 1 || maximumRequests < 1 || decodedBytes < 1)
            throw new ArgumentOutOfRangeException(nameof(addresses));
        _products = products; _use = use; _uploadBudget = uploadBytesPerFrame; _idleFrames = idleFrames;
        _lastUsed = new long[addresses];
        var maximumPageBytes = checked(4 * Unsafe.SizeOf<float3>() + 64 * 64 * 25);
        _maximumDecodedPageBytes = (int)System.Math.Min(maximumPageBytes, decodedBytes);
        _requests = new(async (key, token) => {
            Context context; lock (_gate) context = _contexts[key];
            var record = await read(context.Member, context.Page, token).ConfigureAwait(false);
            context.Member.ValidatePage(context.Page, record.Data); token.ThrowIfCancellationRequested(); return new(context, record);
        }, prepared => checked((int)prepared.Record.Data.PayloadBytes),
            _maximumDecodedPageBytes, maximumRequests, decodedBytes);
        Pool = new(addresses, Validate, slots, write, map, afterSubmittedWork);
    }

    internal void BeginFrame()
    {
        RequireRunning(); if (_inFrame) throw new InvalidOperationException("Coefficient frame is already open.");
        Pool.Reclaim(); _clock = checked(_clock + 1); _uploaded = 0; _wanted.Clear(); _inFrame = true;
    }

    internal void Demand(int product, int page, float priority)
    {
        RequireRunning(); if (!_inFrame) throw new InvalidOperationException("Begin coefficient frame before demand.");
        if (!float.IsFinite(priority) || priority < 0) throw new ArgumentException("Invalid coefficient demand.");
        if (_products.Resident(product) is not { } resident) return;
        if ((uint)page >= resident.Data.Pages.Length) throw new ArgumentOutOfRangeException(nameof(page));
        var descriptor = resident.Data.Pages[page];
        var bytes = checked(4 * Unsafe.SizeOf<float3>() + descriptor.Edge * descriptor.Edge * 25);
        if (bytes > _maximumDecodedPageBytes) return;
        var key = resident.Pages.Generation.ToString("X16") + "/" + page;
        Context context;
        lock (_gate) {
            if (!_contexts.TryGetValue(key, out context!)) {
                context = new(product, resident.Data, resident.Pages, page, key); _contexts.Add(key, context);
            }
        }
        if (_addresses.TryGetValue(context.Address, out var previous) && previous.Pages.Generation != context.Pages.Generation)
            throw new InvalidOperationException("Coefficient address reused before parent withdrawal completed.");
        _addresses[context.Address] = context;
        if (!_wanted.TryGetValue(key, out var old) || priority > old) _wanted[key] = priority;
        _lastUsed[context.Address] = _clock;
    }

    private readonly int _maximumDecodedPageBytes;

    private bool Current(Context context) => _products.Resident(context.Product) is { } resident && resident.Pages == context.Pages;

    private void Validate(int address, PbrLightmapPageData page)
    {
        if (!_addresses.TryGetValue(address, out var context) || !Current(context))
            throw new InvalidDataException("Coefficient page belongs to an obsolete product address generation.");
        context.Member.ValidatePage(context.Page, page);
    }

    internal void EndFrame()
    {
        RequireRunning(); if (!_inFrame) throw new InvalidOperationException("No coefficient frame is open."); _inFrame = false;
        var completed = _requests.Statistics.Completed;
        try {
            Update();
            // Rebuild after admission: active/resident pages must not be requested again.
            if (_requests.Statistics.Completed != completed) UpdateRequests();
        }
        catch (Exception error) { _failure = error; throw; }
    }

    private void Update()
    {
        if (Pool.ActivePage is >= 0 and var active && !WantedAddress(active)) Evict(active, _uploadBudget - _uploaded, out _);
        for (var slot = 0; slot < Pool.Capacity; slot++) {
            var page = Pool.ResidentPage(slot);
            if (page >= 0 && !WantedAddress(page) && _clock - _lastUsed[page] >= _idleFrames)
                Evict(page, _uploadBudget - _uploaded, out _);
        }
        UpdateRequests(); _uploaded += Pool.Advance(_uploadBudget - _uploaded);
        if (Pool.ActivePage >= 0) return;
        _ready.Clear();
        foreach (var key in _requests.ReadyIds) if (_wanted.ContainsKey(key)) _ready.Add(key);
        _ready.Sort((a, b) => { var order = _wanted[b].CompareTo(_wanted[a]); return order == 0 ? StringComparer.Ordinal.Compare(a, b) : order; });
        foreach (var key in _ready) {
            if (_uploadBudget - _uploaded < 32) break;
            if (!_requests.TryGetReady(key, out var prepared)) continue;
            var context = prepared!.Context;
            if (!Current(context)) { _requests.Acknowledge(key); continue; }
            if (Pool.OccupiedSlots == Pool.Capacity) {
                var victim = -1; var lowest = _wanted[key];
                for (var slot = 0; slot < Pool.Capacity; slot++) {
                    var address = Pool.ResidentPage(slot); if (address < 0) continue;
                    var candidate = _addresses[address]; var priority = _wanted.GetValueOrDefault(candidate.Key, -1);
                    if (priority < lowest || priority == lowest && StringComparer.Ordinal.Compare(candidate.Key, key) > 0) {
                        victim = address; lowest = priority;
                    }
                }
                if (victim >= 0) { Evict(victim, _uploadBudget - _uploaded, out _); Pool.Reclaim(); }
            }
            if (Pool.OccupiedSlots == Pool.Capacity) break;
            var data = _use(context.Member, context.Page, prepared.Record);
            if (!Pool.BeginUpload(context.Address, data)) break;
            _requests.Acknowledge(key); _uploaded += Pool.Advance(_uploadBudget - _uploaded);
            if (Pool.ActivePage >= 0) break;
        }
    }

    private bool WantedAddress(int address) => _addresses.TryGetValue(address, out var context) && _wanted.ContainsKey(context.Key) && Current(context);

    private bool Evict(int address, uint budget, out uint bytes)
    {
        var result = Pool.Evict(address, budget, out bytes); _uploaded += bytes; return result;
    }

    private void UpdateRequests()
    {
        _demands.Clear();
        foreach (var (key, priority) in _wanted) {
            Context context; lock (_gate) context = _contexts[key];
            if (!Current(context) || Pool.ResidentSlot(context.Address) >= 0 || Pool.ActivePage == context.Address) continue;
            var descriptor = context.Descriptor;
            _demands.Add(new(key, 4 * Unsafe.SizeOf<float3>() + descriptor.Edge * descriptor.Edge * 25, priority));
        }
        _requests.Update(_demands);
        lock (_gate) {
            _removed.Clear();
            foreach (var (key, context) in _contexts)
                if (!_wanted.ContainsKey(key) && !_requests.HasRequest(key) && Pool.ResidentSlot(context.Address) < 0 && Pool.ActivePage != context.Address)
                    _removed.Add(key);
            foreach (var key in _removed) {
                var context = _contexts[key]; _contexts.Remove(key);
                if (_addresses.GetValueOrDefault(context.Address) == context) _addresses.Remove(context.Address);
            }
        }
    }

    // Invoked after the parent's root pointer was withdrawn. It may span frames:
    // maps first, then terminal reads, then the parent captures its queue fence.
    internal bool Withdraw(StreamRange range, uint budget, out uint uploaded)
    {
        RequireRunning(); uploaded = 0;
        try { return WithdrawCore(range, budget, out uploaded); }
        catch (Exception error) { _failure = error; throw; }
    }

    private bool WithdrawCore(StreamRange range, uint budget, out uint uploaded)
    {
        uploaded = 0;
        budget = System.Math.Min(budget, _uploadBudget - _uploaded);
        foreach (var context in _addresses.Values.Where(context => context.Pages == range)) _wanted.Remove(context.Key);
        if (Pool.ActivePage is >= 0 and var active && _addresses[active].Pages == range)
            if (!Evict(active, budget - uploaded, out var bytes)) return false; else uploaded += bytes;
        for (var slot = 0; slot < Pool.Capacity; slot++) {
            var address = Pool.ResidentPage(slot);
            if (address < 0 || _addresses[address].Pages != range) continue;
            if (!Evict(address, budget - uploaded, out var bytes)) return false;
            uploaded += bytes;
        }
        UpdateRequests();
        lock (_gate) return !_contexts.Values.Any(context => context.Pages == range && _requests.HasRequest(context.Key));
    }

    internal Task StopAsync()
    {
        if (_stop is not null) return _stop; _stopping = true; _inFrame = false;
        return _stop = DrainAsync(Pool.StopAsync(), _requests.DisposeAsync().AsTask());
    }
    private async Task DrainAsync(Task queue, Task reads) { await Task.WhenAll(queue, reads).ConfigureAwait(false); _stopped = true; }
    private void RequireRunning()
    {
        ObjectDisposedException.ThrowIf(_stopping, this);
        if (_failure is not null) throw new InvalidOperationException("Coefficient frame effects failed.", _failure);
    }
}
