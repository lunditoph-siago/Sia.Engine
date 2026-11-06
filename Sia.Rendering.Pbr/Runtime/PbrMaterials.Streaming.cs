using Sia;
using Sia.WebGPU;
using System.Diagnostics;

namespace Sia.Engine.Rendering.Pbr;

/// <summary>Independent CPU preparation and incremental GPU upload limits for texture mip residency.</summary>
/// <remarks>Frame, elapsed-time and scheduling allowances combine; existing GPU material storage admission remains separate.</remarks>
public sealed record PbrTextureStreamingSettings
{
    public uint UploadBytesPerFrame { get; init; } = 1024 * 1024;
    /// <summary>Elapsed-time input/copy credit, additionally limited by UploadBytesPerFrame. Zero uses the frame cap alone.</summary>
    public uint UploadBytesPerSecond { get; init; } = 32 * 1024 * 1024;
    /// <summary>CPU scheduling allowance for incremental uploads. Zero uses only the byte caps.</summary>
    public double UploadMillisecondsPerFrame { get; init; } = 1;
    /// <summary>Maximum synchronous texture input during material initialization; separate from the frame allowance.</summary>
    public ulong BootstrapUploadBytes { get; init; } = 32ul * 1024 * 1024;
    /// <summary>Maximum decoded mip data retained by pending requests, including requests being cancelled.</summary>
    public long DecodedBytes { get; init; } = 64L * 1024 * 1024;
    public int IdleFrames { get; init; } = 60;
    /// <summary>Consecutive obsolete demand frames before cancellation; prevents short camera changes from restarting useful reads.</summary>
    public int StableDemandFrames { get; init; } = 3;
    public float MipBias { get; init; } = 1;
}

public readonly record struct PbrTextureStreamingStatistics(
    ulong AllocatedBytes,
    ulong PeakBytes,
    ulong RetiringBytes,
    uint UploadedBytes,
    long ReservedDecodedBytes,
    int PendingRequests,
    long Installs,
    long Evictions,
    long Failures,
    int DeferredArrays,
    ulong Revision)
{
    public uint CpuUploadedBytes { get; init; }
    public uint CopiedBytes { get; init; }
    public int BudgetLimitedLayers { get; init; }
    public ulong StartupUploadedBytes { get; init; }
    public uint FrameUploadAllowanceBytes { get; init; }
    public int WantedLayers { get; init; }
    public int MissingWantedLayers { get; init; }
    public int MissingAdmittedLayers { get; init; }
    public int MaximumMipGap { get; init; }
    public int DecodedLimitedLayers { get; init; }
    public long PeakReservedDecodedBytes { get; init; }
    public long CancelledRequests { get; init; }
    public long CancelledReservationBytes { get; init; }
}

internal sealed partial class PbrMaterials
{
    private sealed class Request(
        ArrayTexture array,
        int mip,
        int layer,
        int[] mips,
        long bytes,
        CancellationTokenSource cancellation,
        Task<ReadOnlyMemory<byte>[][]> read)
    {
        public readonly ArrayTexture Array = array;
        public readonly int ReadMip = mip;
        public int Mip = mip;
        public readonly int Layer = layer;
        public readonly int[] Mips = mips.ToArray();
        public readonly long Bytes = bytes;
        public readonly CancellationTokenSource Cancellation = cancellation;
        public Task<ReadOnlyMemory<byte>[][]> Read = read;
        public TextureArrayUpload? Upload;
        public int ObsoleteFrames;
    }

    private readonly GpuFrame _frame;
    private readonly Entity _layout;
    private readonly ulong _budget;
    private readonly PbrSceneStream? _stream;
    private readonly PbrTextureStreamingSettings _streaming;
    private readonly List<ArrayTexture> _arrays = [];
    private readonly List<(TextureArrayUpload Upload, Task<bool> Completion)> _retired = [];
    private readonly (ArrayTexture Texture, uint Layer)[][] _materialUses;
    private readonly PbrMaterialGpu[] _parameters;
    private readonly List<ArrayTexture[]> _batchMaps;
    private readonly List<Request> _pending = [];
    private readonly bool[] _dirtyMaterials, _dirtyGroups;
    private bool _planDirty = true;
    private bool _bindingsDirty;
    private long _clock;
    private long _installs, _evictions, _failures;
    private uint _uploaded, _copied;
    private ulong _startupUploaded;
    private ulong _peak;
    private Task? _stop;
    private bool _stopped;
    private long _lastUploadFrame = Stopwatch.GetTimestamp();
    private double _uploadCredit;
    private uint _uploadAllowance;
    private long _peakDecoded, _cancelled, _cancelledReservationBytes;

    public ulong Revision { get; private set; }

    public PbrTextureStreamingStatistics Statistics {
        get {
            ulong retiring = 0;
            foreach (var (Upload, Completion) in _retired) retiring = checked(retiring + Upload.Bytes);
            var deferred = 0;
            var limited = 0;
            var wanted = 0; var missingWanted = 0; var missingAdmitted = 0; var maximumGap = 0; var decodedLimited = 0;
            foreach (var array in _arrays) {
                var missing = false;
                for (var layer = 0; layer < array.ResidentMips.Length; layer++) {
                    missing |= array.WantedMips[layer] < array.ResidentMips[layer];
                    if (array.DesiredMips[layer] > array.WantedMips[layer]) limited++;
                    wanted++;
                    if (array.WantedMips[layer] < array.ResidentMips[layer]) missingWanted++;
                    if (array.DesiredMips[layer] < array.ResidentMips[layer]) missingAdmitted++;
                    maximumGap = System.Math.Max(maximumGap, array.ResidentMips[layer] - array.WantedMips[layer]);
                    var wantedMip = array.WantedMips[layer];
                    if (wantedMip < array.TailMip && array.Layout.MipByteLength(System.Math.Max(1u, array.Width >> wantedMip),
                        System.Math.Max(1u, array.Height >> wantedMip)) > (ulong)_streaming.DecodedBytes) decodedLimited++;
                }
                if (missing) deferred++;
            }
            return new(Bytes, _peak, retiring, _uploaded, _stopped ? 0 : _pending.Sum(p => p.Bytes),
                _stopped ? 0 : _pending.Count, _installs, _evictions, _failures, deferred, Revision) {
                CpuUploadedBytes = _uploaded - _copied, CopiedBytes = _copied, BudgetLimitedLayers = limited,
                StartupUploadedBytes = _startupUploaded, FrameUploadAllowanceBytes = _uploadAllowance,
                WantedLayers = wanted, MissingWantedLayers = missingWanted, MissingAdmittedLayers = missingAdmitted,
                MaximumMipGap = maximumGap, DecodedLimitedLayers = decodedLimited, PeakReservedDecodedBytes = _peakDecoded,
                CancelledRequests = _cancelled, CancelledReservationBytes = _cancelledReservationBytes
            };
        }
    }

    internal bool IsStopped => _stream is null || _stopped;
    internal bool IsStreaming => _stream is not null;

    private (uint Width, uint Height, int Levels, WGPUTextureFormat Format, PbrTextureSampler Sampler, int TailMip, bool Streamed) Shape(PbrTextureData t)
        => _stream is not null && _stream.TextureSources.TryGetValue(t, out var info)
            ? (info.Width, info.Height, info.Levels, t.GpuFormat, t.Sampler, info.TailMip, true)
            : (t.Width, t.Height, t.MipLevels.Length, t.GpuFormat, t.Sampler, 0, false);

    private static long TailBytes(uint width, uint height, int levels, int first, TextureBlockLayout layout)
    {
        long bytes = 0;
        for (var mip = first; mip < levels; mip++)
            bytes = checked(bytes + (long)layout.MipByteLength(System.Math.Max(1u, width >> mip), System.Math.Max(1u, height >> mip)));
        return bytes;
    }

    private ArrayTexture UploadStreamTail(PbrTextureData[] sources)
    {
        var first = sources[0];
        var info = _stream!.TextureSources[first];
        var upload = new TextureArrayUpload(_frame, first.Width, first.Height, first.GpuFormat,
            [.. sources.Select(t => t.MipLevels.ToArray())], PbrTextureStreamingPolicy.AvailableBytes(_budget, Bytes));
        try {
            while (!upload.Complete) _startupUploaded = checked(_startupUploaded + upload.Advance(uint.MaxValue));
            var sampler = WGPUSamplerDescriptor.Default;
            sampler.AddressModeU = first.Sampler.AddressU; sampler.AddressModeV = first.Sampler.AddressV;
            sampler.MinFilter = first.Sampler.MinFilter; sampler.MagFilter = first.Sampler.MagFilter;
            sampler.MipmapFilter = first.Sampler.MipFilter;
            sampler.LodMaxClamp = sources.Length > 1 || first.Sampler.UseMipmaps ? info.Levels - 1 : 0;
            var result = new ArrayTexture(upload.Texture, upload.View, _gpu.Own(Wgpu.CreateSampler(_gpu.Device, sampler))) {
                Sources = sources, Width = info.Width, Height = info.Height, Levels = info.Levels,
                Layout = first.BlockLayout,
                TailMip = info.TailMip, ResidentMip = info.TailMip,
                DesiredMip = info.TailMip, Residency = upload,
                ResidentMips = Enumerable.Repeat(info.TailMip, sources.Length).ToArray(),
                WantedMips = Enumerable.Repeat(info.TailMip, sources.Length).ToArray(),
                DesiredMips = Enumerable.Repeat(info.TailMip, sources.Length).ToArray(),
                PlanMips = Enumerable.Repeat(info.TailMip, sources.Length).ToArray(),
                PriorityBands = new int[sources.Length],
                Idle = new int[sources.Length], Priorities = new float[sources.Length]
            };
            result.TailCosts = new ulong[info.TailMip + 1];
            for (var mip = 0; mip <= info.TailMip; mip++)
                result.TailCosts[mip] = checked((ulong)TailBytes(result.Width, result.Height, result.Levels, mip, result.Layout) * (ulong)sources.Length);
            result.MinimumMip = PbrTextureStreamingPolicy.MinimumMip(result.Width, result.Height, result.TailMip,
                _gpu.Limits.MaxTextureDimension2D, _streaming.DecodedBytes, result.Layout);
            Textures.Add(result.Texture);
            return result;
        }
        catch { upload.Dispose(); throw; }
    }

    public void BeginFrame()
    {
        if (_stream is null) return;
        if (_stop is not null) throw new ObjectDisposedException(nameof(PbrMaterials));
        _clock++;
        var now = Stopwatch.GetTimestamp();
        _uploadAllowance = PbrTextureStreamingPolicy.UploadAllowance(_streaming.UploadBytesPerFrame,
            _streaming.UploadBytesPerSecond, (double)(now - _lastUploadFrame) / Stopwatch.Frequency, ref _uploadCredit);
        _lastUploadFrame = now;
        _uploaded = _copied = 0;
        for (var i = _retired.Count - 1; i >= 0; i--) {
            if (!_retired[i].Completion.IsCompleted) continue;
            if (!_retired[i].Completion.GetAwaiter().GetResult())
                throw new InvalidOperationException("GPU queue retirement failed; texture storage cannot be reclaimed safely.");
            _retired[i].Upload.Dispose();
            _retired.RemoveAt(i);
        }
        for (var requestIndex = _pending.Count - 1; requestIndex >= 0; requestIndex--) {
            var pending = _pending[requestIndex];
            if (pending.Upload is not { Complete: true } next || pending.Cancellation.IsCancellationRequested) continue;
            var array = pending.Array;
            var replacement = next.Texture != array.Texture;
            if (replacement) {
                _retired.Add((array.Residency!, GpuQueueCompletion.AfterSubmittedWork(_gpu.Queue)));
                array.Texture = next.Texture; array.View = next.View; array.Residency = next;
                Textures[array.Index] = next.Texture;
            }
            else next.Dispose();
            var evicted = false;
            for (var layer = 0; layer < pending.Mips.Length; layer++) {
                evicted |= pending.Mips[layer] > array.ResidentMips[layer];
            }
            if (evicted) _evictions++;
            array.ResidentMip = pending.Mip;
            pending.Mips.CopyTo(array.ResidentMips, 0);
            _installs++;
            Revision++;
            if (replacement) RebuildGroups(array);
            UpdateMipClamps(array);
            pending.Cancellation.Dispose();
            _pending.RemoveAt(requestIndex);
        }
        FlushBindings();
        foreach (var array in _arrays) {
            Array.Fill(array.WantedMips, array.TailMip); Array.Clear(array.Priorities);
        }
        _peak = System.Math.Max(_peak, Bytes);
    }

    public void Demand(int material, float pixels)
    {
        if (_stream is null || pixels <= 0 || float.IsNaN(pixels)) return;
        foreach (var (array, layer) in _materialUses[material]) {
            if (array.Residency is null) continue;
            var footprint = float.IsFinite(pixels) ? pixels : System.Math.Max(array.Width, array.Height);
            var ratio = System.Math.Max(array.Width, array.Height) / System.Math.Max(1f, footprint);
            var mip = float.IsFinite(ratio) ? (int)MathF.Floor(MathF.Log2(ratio) - _streaming.MipBias) : 0;
            array.WantedMips[layer] = System.Math.Min(array.WantedMips[layer], System.Math.Clamp(mip, 0, array.TailMip));
            array.Priorities[layer] = System.Math.Max(array.Priorities[layer], footprint);
        }
    }

    public void EndFrame()
    {
        if (_stream is null || _stop is not null) return;
        foreach (var array in _arrays) {
            for (var layer = 0; layer < array.Sources.Length; layer++) {
                // The idle grace protects resident detail, not uninstalled IO.
                // PlanMips caches the resulting input without extending demand.
                var wanted = PbrTextureStreamingPolicy.DemandMip(array.WantedMips[layer], array.ResidentMips[layer],
                    array.MinimumMip, _streaming.IdleFrames, ref array.Idle[layer]);
                var band = PbrTextureStreamingPolicy.PriorityBand(array.Priorities[layer], array.PriorityBands[layer]);
                _planDirty |= wanted != array.PlanMips[layer] || band != array.PriorityBands[layer];
                array.PlanMips[layer] = wanted; array.PriorityBands[layer] = band;
            }
        }
        if (_planDirty) BudgetDemand();
        foreach (var pending in _pending) {
            // Once finer GPU writes begin, finish their atomic publication.
            // Downgrades still cancel when a returning view needs their mips.
            if (pending.Layer >= 0 && pending.Upload is not null) continue;
            var obsolete = PbrTextureStreamingPolicy.IsObsolete(pending.Array.ResidentMips, pending.Array.DesiredMips, pending.Mips,
                pending.Layer < 0 ? pending.Mip : -1);
            if (!pending.Cancellation.IsCancellationRequested
                && PbrTextureStreamingPolicy.StableObsolete(obsolete, _streaming.StableDemandFrames, ref pending.ObsoleteFrames)) {
                _cancelled++; _cancelledReservationBytes += pending.Bytes;
                pending.Cancellation.Cancel();
            }
        }
        for (var requestIndex = _pending.Count - 1; requestIndex >= 0; requestIndex--) {
            var request = _pending[requestIndex];
            if (request.Cancellation.IsCancellationRequested) {
                if (!request.Read.IsCompleted) continue;
                _ = request.Read.Exception;
                if (request.Upload is not null) {
                    request.Upload.Abandon();
                    _retired.Add((request.Upload, GpuQueueCompletion.AfterSubmittedWork(_gpu.Queue)));
                }
                request.Cancellation.Dispose(); _pending.RemoveAt(requestIndex);
            }
            else if (request.Read.IsCompleted && !request.Read.IsCompletedSuccessfully) {
                _ = request.Read.Exception;
                _failures++;
                request.Array.RetryFrame = _clock + 60;
                request.Cancellation.Dispose(); _pending.RemoveAt(requestIndex);
            }
        }
        while (_pending.Count < 2) {
            ArrayTexture? best = null;
            var reserved = _pending.Where(p => p.Upload is null).Aggregate(0ul, (bytes, p) => checked(bytes + AllocationBytes(p)));
            var available = PbrTextureStreamingPolicy.AvailableBytes(_budget, Bytes, reserved);
            var decodedAvailable = System.Math.Max(0, _streaming.DecodedBytes - _pending.Sum(p => p.Bytes));
            foreach (var array in _arrays) {
                var demand = DescribeDemand(array);
                if (array.Residency is null || !demand.Changed || array.RetryFrame > _clock
                    || _pending.Any(p => ReferenceEquals(p.Array, array)))
                    continue;
                var allocation = demand.Mip == array.ResidentMip ? 0 : array.TailCosts[demand.Mip];
                if (demand.Bytes > decodedAvailable || allocation > available) continue;
                if (best is null || CompareDemand(array, best) > 0) best = array;
            }
            if (best is null) break;
            StartRequest(best, decodedAvailable, available);
        }

        // Share this BeginFrame's allowance between already admitted ready arrays.
        // Completed uploads stay owned by their requests until the following BeginFrame.
        var uploadStarted = Stopwatch.GetTimestamp();
        while (_uploadAllowance - _uploaded >= 4) {
            if (_uploaded != 0 && _streaming.UploadMillisecondsPerFrame > 0
                && Stopwatch.GetElapsedTime(uploadStarted).TotalMilliseconds >= _streaming.UploadMillisecondsPerFrame) break;
            var available = PbrTextureStreamingPolicy.AvailableBytes(_budget, Bytes);
            Request? active = null;
            foreach (var request in _pending) {
                if (!request.Read.IsCompletedSuccessfully || request.Cancellation.IsCancellationRequested
                    || request.Upload is { Complete: true }
                    || (request.Layer >= 0 && request.Upload is null
                        && request.Array.DesiredMips[request.Layer] >= request.Array.ResidentMips[request.Layer])
                    || request.Array.Layout.BytesPerBlock > _uploadAllowance - _uploaded
                    || (request.Upload is null && AllocationBytes(request, UploadMip(request)) > available)) continue;
                if (active is null || CompareLayer(request.Array, request.Layer, request.Bytes, active.Array, active.Layer, active.Bytes) > 0)
                    active = request;
            }
            if (active is null) break;
            if (active.Upload is null) {
                active.Mip = UploadMip(active);
                if (active.Layer >= 0)
                    active.Mips[active.Layer] = PbrTextureStreamingPolicy.TrimUpgradeMip(active.Array.ResidentMips[active.Layer],
                        active.Array.DesiredMips[active.Layer], active.Mips[active.Layer]);
                var levels = PbrTextureStreamingPolicy.TrimUpload(active.Read.Result, active.ReadMip, active.Mip,
                    active.Array.ResidentMips, active.Mips);
                var width = System.Math.Max(1u, active.Array.Width >> active.Mip);
                var height = System.Math.Max(1u, active.Array.Height >> active.Mip);
                active.Upload = active.Mip == active.Array.ResidentMip
                    ? TextureArrayUpload.Update(_frame, width, height, active.Array.Texture, active.Array.View, active.Array.Sources[0].GpuFormat, levels)
                    : new(_frame, width, height, active.Array.Sources[0].GpuFormat, levels, available,
                        active.Array.Texture, active.Array.ResidentMip - active.Mip, active.Array.Levels - active.Mip);
            }
            var advanced = active.Upload.Advance(System.Math.Min(256 * 1024u, _uploadAllowance - _uploaded));
            _uploaded += advanced;
            _copied += active.Upload.CopiedBytes;
            _peak = System.Math.Max(_peak, Bytes);
            if (advanced == 0 && !active.Upload.Complete) break;
        }
        if (_streaming.UploadBytesPerSecond != 0) _uploadCredit = System.Math.Max(0, _uploadCredit - _uploaded);
    }

    private static int CompareDemand(ArrayTexture a, ArrayTexture b)
    {
        var first = DescribeDemand(a); var second = DescribeDemand(b);
        return CompareLayer(a, first.Layer, first.Bytes, b, second.Layer, second.Bytes);
    }

    private static int CompareLayer(ArrayTexture a, int aLayer, long aBytes, ArrayTexture b, int bLayer, long bBytes)
    {
        var coarser = (aLayer < 0).CompareTo(bLayer < 0);
        if (coarser != 0) return coarser;
        var visibility = (aLayer >= 0 && a.Priorities[aLayer] > 0).CompareTo(bLayer >= 0 && b.Priorities[bLayer] > 0);
        if (visibility != 0) return visibility;
        var aGap = aLayer < 0 ? 0 : a.ResidentMips[aLayer] - a.DesiredMips[aLayer];
        var bGap = bLayer < 0 ? 0 : b.ResidentMips[bLayer] - b.DesiredMips[bLayer];
        var gap = aGap.CompareTo(bGap);
        if (gap != 0) return gap;
        var aScore = aLayer < 0 ? 0 : System.Math.ScaleB(1, a.PriorityBands[aLayer]) / System.Math.Max(1, aBytes);
        var bScore = bLayer < 0 ? 0 : System.Math.ScaleB(1, b.PriorityBands[bLayer]) / System.Math.Max(1, bBytes);
        return aScore.CompareTo(bScore);
    }

    private void BudgetDemand()
    {
        var available = PbrTextureStreamingPolicy.AvailableBytes(_budget, _gpu.Bytes);
        var arrays = _arrays.Where(array => array.Residency is not null).ToArray();
        var inputs = arrays.Select(array => new PbrTextureStreamingPolicy.BudgetInput(
            array.TailCosts, array.PlanMips, array.DesiredMips, array.PriorityBands)).ToArray();
        var targets = PbrTextureStreamingPolicy.BudgetTargets(inputs, available);
        for (var index = 0; index < arrays.Length; index++) {
            targets[index].CopyTo(arrays[index].DesiredMips, 0);
            arrays[index].DesiredMip = targets[index].Min();
        }
        _planDirty = false;
    }

    private void StartRequest(ArrayTexture array, long decodedAvailable, ulong allocationAvailable)
    {
        var demand = DescribeDemand(array);
        var mip = demand.Mip; var bytes = demand.Bytes;
        var mips = array.ResidentMips.ToArray();
        if (demand.Layer >= 0) {
            // Publish small refinements promptly when demand changes during IO;
            // one independently readable mip may still exceed the frame quota.
            var range = PbrTextureStreamingPolicy.UpgradeRange(array.Width, array.Height, array.Levels,
                array.ResidentMips[demand.Layer], array.DesiredMips[demand.Layer], array.ResidentMip,
                array.Sources.Length, decodedAvailable, allocationAvailable, array.Layout,
                System.Math.Max(demand.Bytes, _streaming.UploadBytesPerFrame));
            mips[demand.Layer] = range.Mip;
            mip = System.Math.Min(array.ResidentMip, range.Mip);
            bytes = range.Bytes;
        }
        else for (var layer = 0; layer < mips.Length; layer++) mips[layer] = System.Math.Max(mip, mips[layer]);
        var resident = array.ResidentMips.ToArray();
        var cancellation = new CancellationTokenSource();
        var read = Task.Run(async () => {
            var result = new ReadOnlyMemory<byte>[array.Sources.Length][];
            for (var layer = 0; layer < result.Length; layer++) {
                result[layer] = new ReadOnlyMemory<byte>[array.Levels - mip];
                for (var level = mips[layer]; level < resident[layer]; level++)
                    result[layer][level - mip] = await _stream!.ReadTextureMipAsync(array.Sources[layer], level, cancellation.Token).ConfigureAwait(false);
            }
            return result;
        }, cancellation.Token);
        _pending.Add(new(array, mip, demand.Layer, mips, bytes, cancellation, read));
        _peakDecoded = System.Math.Max(_peakDecoded, _pending.Sum(request => request.Bytes));
    }

    private static int UploadMip(Request request) => request.Upload is not null || request.Layer < 0 ? request.Mip
        : System.Math.Min(request.Array.ResidentMip, PbrTextureStreamingPolicy.TrimUpgradeMip(
            request.Array.ResidentMips[request.Layer], request.Array.DesiredMips[request.Layer], request.Mips[request.Layer]));

    private static ulong AllocationBytes(Request request) => AllocationBytes(request, request.Mip);

    private static ulong AllocationBytes(Request request, int mip) => mip == request.Array.ResidentMip ? 0 : request.Array.TailCosts[mip];

    private static (int Mip, long Bytes, bool Changed, int Layer) DescribeDemand(ArrayTexture array)
    {
        if (array.DesiredMip > array.ResidentMip) return (array.DesiredMip, 0, true, -1);
        var selected = -1;
        var bestGap = 0; double bestScore = 0;
        for (var layer = 0; layer < array.Sources.Length; layer++) {
            var gap = array.ResidentMips[layer] - array.DesiredMips[layer];
            if (gap <= 0) continue;
            var next = array.ResidentMips[layer] - 1;
            var cost = (array.TailCosts[next] - array.TailCosts[next + 1]) / (ulong)array.Sources.Length;
            var score = System.Math.ScaleB(1, array.PriorityBands[layer]) / cost;
            var visible = array.Priorities[layer] > 0;
            var selectedVisible = selected >= 0 && array.Priorities[selected] > 0;
            if (selected < 0 || (visible && !selectedVisible)
                || (visible == selectedVisible && (gap > bestGap || (gap == bestGap && score > bestScore)))) {
                selected = layer; bestGap = gap; bestScore = score;
            }
        }
        if (selected < 0) return (array.ResidentMip, 0, false, -1);
        var nextMip = array.ResidentMips[selected] - 1;
        var bytes = checked((long)((array.TailCosts[nextMip] - array.TailCosts[nextMip + 1]) / (ulong)array.Sources.Length));
        return (System.Math.Min(array.ResidentMip, nextMip), bytes, true, selected);
    }

    private void UpdateMipClamps(ArrayTexture changed)
    {
        foreach (var material in changed.MaterialRows) {
            _dirtyMaterials[material] = true;
            _bindingsDirty = true;
        }
    }

    private void FlushBindings()
    {
        if (!_bindingsDirty) return;
        for (var material = 0; material < _dirtyMaterials.Length; material++) {
            if (!_dirtyMaterials[material]) continue;
            var uses = _materialUses[material];
            uint packed = 0;
            for (var map = 0; map < uses.Length; map++) {
                var (array, layer) = uses[map];
                if (array.Residency is null) continue;
                packed |= (uint)(array.ResidentMips[layer] - array.ResidentMip) << (map * 4);
                if (array.Sources.Length > 1 && !array.Sources[layer].Sampler.UseMipmaps) packed |= 1u << (20 + map);
            }
            packed |= _parameters[material].Indices.w & (1u << 25);
            var old = _parameters[material];
            if (packed == old.Indices.w) { _dirtyMaterials[material] = false; continue; }
            _parameters[material] = old with { Indices = new(old.Indices.x, old.Indices.y, old.Indices.z, packed) };
        }
        for (var first = 0; first < _dirtyMaterials.Length;) {
            if (!_dirtyMaterials[first]) { first++; continue; }
            var end = first + 1;
            while (end < _dirtyMaterials.Length && _dirtyMaterials[end]) end++;
            Wgpu.WriteBuffer<PbrMaterialGpu>(_gpu.Queue, Table.GetWgpu<WGPUBuffer>(),
                (ulong)first * (ulong)System.Runtime.InteropServices.Marshal.SizeOf<PbrMaterialGpu>(), _parameters.AsSpan(first, end - first));
            Array.Clear(_dirtyMaterials, first, end - first);
            first = end;
        }
        Span<WGPUBindGroupEntry> entries = stackalloc WGPUBindGroupEntry[12];
        for (var batch = 0; batch < Groups.Length; batch++) {
            if (!_dirtyGroups[batch]) continue;
            var maps = _batchMaps[batch];
            entries.Clear();
            entries[0] = GpuBinding.Buffer(0, Table); entries[1] = GpuBinding.Buffer(1, Uniforms[batch]);
            for (var map = 0; map < 5; map++) {
                entries[2 + (map * 2)] = GpuBinding.Texture((uint)(2 + (map * 2)), maps[map].View.GetWgpu<WGPUTextureView>());
                entries[3 + (map * 2)] = GpuBinding.Sampler((uint)(3 + (map * 2)), maps[map].Sampler);
            }
            var next = GpuBinding.Group(_gpu, _layout, entries);
            _gpu.Release(Groups[batch]);
            Groups[batch] = next;
            _dirtyGroups[batch] = false;
        }
        _bindingsDirty = false;
    }

    private void RebuildGroups(ArrayTexture changed)
    {
        foreach (var batch in changed.Batches) {
            _dirtyGroups[batch] = true;
            _bindingsDirty = true;
        }
    }

    public ValueTask StopAsync() => new(_stop ??= DrainAsync());

    private async Task DrainAsync()
    {
        foreach (var pending in _pending) {
            if (pending.Cancellation.IsCancellationRequested) continue;
            _cancelled++; _cancelledReservationBytes += pending.Bytes;
            pending.Cancellation.Cancel();
        }
        foreach (var pending in _pending) {
            try { await pending.Read.ConfigureAwait(false); }
            catch (Exception) { _ = pending.Read.Exception; }
            pending.Upload?.Abandon();
            pending.Read = Task.FromResult<ReadOnlyMemory<byte>[][]>([]);
            pending.Cancellation.Dispose();
        }
        _stopped = true;
    }
}
