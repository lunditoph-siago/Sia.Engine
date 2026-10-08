namespace Sia.Asset;

/// <summary>The typed entry point and CPU content owner shared by all engine asset kinds.</summary>
public sealed class AssetLibrary : IAsyncDisposable
{
    private readonly Lock _gate = new();
    private readonly Func<AssetId, CancellationToken, ValueTask<Stream>> _open;
    private readonly AssetLibraryLimits _limits;
    private readonly SemaphoreSlim _reads;
    private readonly Dictionary<Type, Codec> _codecs = [];
    private readonly HashSet<ushort> _kinds = [];
    private readonly Dictionary<AssetId, Entry> _entries = [];
    private readonly HashSet<Entry> _pending = [];
    private readonly List<Task> _cancellations = [];
    private long _encoded, _decoded, _resident, _clock, _readCount, _hits, _coalesced;
    private bool _started, _stopped;
    private int _residentCount;
    private Exception? _cancellationFailure;
    private Task? _shutdown;

    public AssetLibrary(Func<AssetId, CancellationToken, ValueTask<Stream>> open, AssetLibraryLimits limits)
    {
        ArgumentNullException.ThrowIfNull(open);
        if (limits.EncodedBytes < AssetContainer.HeaderBytes + 1 || limits.DecodedBytes < 1
            || limits.ResidentBytes < 1 || limits.ConcurrentReads < 1 || limits.PendingAssets < limits.ConcurrentReads
            || limits.ResidentAssets < 1)
            throw new ArgumentOutOfRangeException(nameof(limits));
        _open = open;
        _limits = limits;
        _reads = new(limits.ConcurrentReads);
    }

    // Registration is per owner and freezes on first acquisition; there is no global registry.
    public AssetLibrary Register<T>(IAssetCodec<T> codec) where T : class
    {
        ArgumentNullException.ThrowIfNull(codec);
        var kind = codec.Kind;
        if (kind == 0) throw new ArgumentException("An asset kind must be nonzero.", nameof(codec));
        lock (_gate) {
            ObjectDisposedException.ThrowIf(_stopped, this);
            if (_started) throw new InvalidOperationException("Asset registration is frozen after the first acquisition.");
            if (_codecs.ContainsKey(typeof(T)) || _kinds.Contains(kind))
                throw new ArgumentException("Asset types and kinds must be unique within a library.", nameof(codec));
            _codecs.Add(typeof(T), new TypedCodec<T>(codec, kind));
            _kinds.Add(kind);
        }
        return this;
    }

    public AssetLibraryStatistics Statistics {
        get {
            lock (_gate) return new(_encoded, _decoded, _resident, _pending.Count,
                _entries.Count - _pending.Count, Interlocked.Read(ref _readCount), _hits, _coalesced);
        }
    }

    public async ValueTask<AssetLease<T>> AcquireAsync<T>(AssetReference<T> reference,
        CancellationToken cancellationToken = default) where T : class
    {
        if (reference.Id == default) throw new ArgumentException("An initialized asset reference is required.", nameof(reference));
        Entry entry;
        while (true) {
            cancellationToken.ThrowIfCancellationRequested();
            Task? retry = null;
            lock (_gate) {
                ObjectDisposedException.ThrowIf(_stopped, this);
                if (!_codecs.TryGetValue(typeof(T), out var codec))
                    throw new InvalidOperationException($"No asset codec is registered for {typeof(T)}.");
                _started = true;
                if (_entries.TryGetValue(reference.Id, out entry!)) {
                    if (entry.Codec.Type != typeof(T) || entry.Reference.EncodedBytes != reference.EncodedBytes
                        || entry.Reference.DecodedBytes != reference.DecodedBytes)
                        throw new InvalidDataException("Conflicting asset type or bounds for the same content ID.");
                    if (entry.Cancellation.IsCancellationRequested) retry = entry.Finished.Task;
                    else if (entry.Value is null) _coalesced++;
                    else _hits++;
                }
                else {
                    var encoded = (long)reference.EncodedBytes + AssetContainer.HeaderBytes + 1;
                    var decoded = Math.Max(1L, reference.DecodedBytes);
                    if (encoded > _limits.EncodedBytes - _encoded) throw new AssetBudgetExceededException(AssetBudgetKind.Encoded);
                    if (decoded > _limits.DecodedBytes - _decoded) throw new AssetBudgetExceededException(AssetBudgetKind.Decoded);
                    if (_pending.Count >= _limits.PendingAssets) throw new AssetBudgetExceededException(AssetBudgetKind.Requests);
                    entry = new(new(reference.Id, reference.EncodedBytes, reference.DecodedBytes), codec, encoded, decoded);
                    _encoded += encoded;
                    _decoded += decoded;
                    _entries.Add(reference.Id, entry);
                    _pending.Add(entry);
                    _ = LoadAsync(entry);
                }
                if (retry is null) {
                    entry.Users++;
                    entry.LastUse = ++_clock;
                    break;
                }
            }
            // A new borrower must not inherit the cancellation of all prior borrowers.
            await retry.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        var transferred = false;
        try {
            await entry.Ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate) ObjectDisposedException.ThrowIf(_stopped, this);
            var lease = new AssetLease<T>(this, entry);
            transferred = true;
            return lease;
        }
        finally { if (!transferred) Release(entry); }
    }

    private async Task LoadAsync(Entry entry)
    {
        await Task.Yield();
        var entered = false;
        Exception? failure = null;
        object? value = null;
        try {
            var token = entry.Cancellation.Token;
            await _reads.WaitAsync(token).ConfigureAwait(false);
            entered = true;
            Interlocked.Increment(ref _readCount);
            byte[] payload;
            await using (var input = await _open(entry.Reference.Id, token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The asset source returned no stream.")) {
                payload = await AssetContainer.ReadAsync(input, entry.Reference, entry.Codec.Kind,
                    entry.Reference.EncodedBytes, entry.Reference.DecodedBytes, token).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            var plan = entry.Codec.Inspect(payload);
            if (plan.OwnedBytes < 0) throw new InvalidDataException("The asset codec declared a negative memory requirement.");
            var cost = checked(plan.OwnedBytes + (plan.RetainsPayload ? payload.Length : 0));
            lock (_gate) {
                ObjectDisposedException.ThrowIf(_stopped, this);
                ReserveResident(cost);
                entry.ResidentBytes = cost;
                entry.ResidentAdmitted = true;
            }
            token.ThrowIfCancellationRequested();
            value = entry.Codec.Decode(payload) ?? throw new InvalidDataException("The asset codec returned no value.");
            token.ThrowIfCancellationRequested();
        }
        catch (Exception error) { failure = error; }
        finally {
            if (entered) _reads.Release();
            lock (_gate) {
                _encoded -= entry.EncodedBytes;
                _decoded -= entry.DecodedBytes;
                _pending.Remove(entry);
                if (failure is null && !_stopped && !entry.Cancellation.IsCancellationRequested) {
                    entry.Value = value;
                    entry.Ready.TrySetResult();
                }
                else {
                    _resident -= entry.ResidentBytes;
                    if (entry.ResidentAdmitted) _residentCount--;
                    entry.ResidentBytes = 0;
                    _entries.Remove(entry.Reference.Id);
                    if (failure is OperationCanceledException && entry.Cancellation.IsCancellationRequested)
                        entry.Ready.TrySetCanceled(entry.Cancellation.Token);
                    else entry.Ready.TrySetException(failure ?? new ObjectDisposedException(nameof(AssetLibrary)));
                }
            }
            entry.Finished.TrySetResult();
        }
    }

    private void ReserveResident(long bytes)
    {
        if (bytes > _limits.ResidentBytes) throw new AssetBudgetExceededException(AssetBudgetKind.Resident);
        while (bytes > _limits.ResidentBytes - _resident || _residentCount >= _limits.ResidentAssets) {
            Entry? victim = null;
            foreach (var candidate in _entries.Values) {
                if (candidate.Users == 0 && candidate.Value is not null
                    && (victim is null || candidate.LastUse < victim.LastUse)) victim = candidate;
            }
            if (victim is null) throw new AssetBudgetExceededException(AssetBudgetKind.Resident);
            _entries.Remove(victim.Reference.Id);
            _resident -= victim.ResidentBytes;
            _residentCount--;
            victim.Value = null;
        }
        _resident += bytes;
        _residentCount++;
    }

    internal T GetValue<T>(Entry entry) where T : class
    {
        lock (_gate) {
            ObjectDisposedException.ThrowIf(_stopped, this);
            return (T)(entry.Value ?? throw new ObjectDisposedException(nameof(AssetLease<T>)));
        }
    }

    internal void Release(Entry entry)
    {
        lock (_gate) {
            entry.Users--;
            entry.LastUse = ++_clock;
            if (entry.Users == 0 && _pending.Contains(entry)) Cancel(entry);
        }
    }

    private void Cancel(Entry entry)
    {
        for (var index = _cancellations.Count - 1; index >= 0; index--) {
            var previous = _cancellations[index];
            if (!previous.IsCompleted) continue;
            if (previous.IsFaulted) _cancellationFailure ??= previous.Exception;
            _cancellations.RemoveAt(index);
        }
        // CancelAsync sets the cancellation flag now and executes arbitrary callbacks outside this lock.
        var cancellation = entry.Cancellation.CancelAsync();
        if (!cancellation.IsCompletedSuccessfully) _cancellations.Add(cancellation);
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate) {
            if (_shutdown is null) {
                _stopped = true;
                var pending = _pending.ToArray();
                foreach (var entry in pending) Cancel(entry);
                _shutdown = ShutdownAsync(pending, _cancellations.ToArray());
            }
            return new(_shutdown);
        }
    }

    private async Task ShutdownAsync(Entry[] pending, Task[] cancellations)
    {
        await Task.Yield();
        try {
            await Task.WhenAll(pending.Select(e => e.Finished.Task).Concat(cancellations)).ConfigureAwait(false);
            if (_cancellationFailure is not null) throw _cancellationFailure;
        }
        finally {
            lock (_gate) {
                foreach (var entry in _entries.Values) entry.Value = null;
                _entries.Clear();
                _cancellations.Clear();
                _resident = 0;
                _residentCount = 0;
            }
            _reads.Dispose();
        }
    }

    internal sealed class Entry(AssetReference<object> reference, Codec codec, long encodedBytes, long decodedBytes)
    {
        internal readonly AssetReference<object> Reference = reference;
        internal readonly Codec Codec = codec;
        internal readonly long EncodedBytes = encodedBytes, DecodedBytes = decodedBytes;
        internal readonly CancellationTokenSource Cancellation = new();
        internal readonly TaskCompletionSource Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal object? Value;
        internal long ResidentBytes, LastUse;
        internal int Users;
        internal bool ResidentAdmitted;
    }

    internal abstract class Codec(Type type, ushort kind)
    {
        internal Type Type { get; } = type;
        internal ushort Kind { get; } = kind;
        internal abstract AssetDecodePlan Inspect(ReadOnlySpan<byte> payload);
        internal abstract object Decode(ReadOnlyMemory<byte> payload);
    }

    private sealed class TypedCodec<T>(IAssetCodec<T> codec, ushort kind) : Codec(typeof(T), kind) where T : class
    {
        internal override AssetDecodePlan Inspect(ReadOnlySpan<byte> payload) => codec.Inspect(payload);
        internal override object Decode(ReadOnlyMemory<byte> payload) => codec.Decode(payload);
    }
}
