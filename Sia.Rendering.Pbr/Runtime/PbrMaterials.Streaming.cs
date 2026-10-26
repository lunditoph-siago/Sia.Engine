using Sia;
using Sia.WebGPU;

namespace Sia.Engine.Rendering.Pbr;

public sealed record PbrTextureStreamingSettings
{
    public uint UploadBytesPerFrame { get; init; } = 256 * 1024;
    public long DecodedBytes { get; init; } = 32L * 1024 * 1024;
    public int IdleFrames { get; init; } = 60;
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
        public readonly int Mip = mip;
        public readonly int Layer = layer;
        public readonly int[] Mips = mips;
        public readonly long Bytes = bytes;
        public readonly CancellationTokenSource Cancellation = cancellation;
        public Task<ReadOnlyMemory<byte>[][]> Read = read;
        public TextureArrayUpload? Upload;
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
    private readonly PriorityQueue<(ArrayTexture Array, int Order), (double Score, int Order)> _budgetDrops = new();
    private readonly PriorityQueue<ArrayTexture, long> _budgetHeadroom = new();
    private long _clock;
    private long _installs, _evictions, _failures;
    private uint _uploaded, _copied;
    private ulong _peak;
    private Task? _stop;
    private bool _stopped;

    public ulong Revision { get; private set; }

    public PbrTextureStreamingStatistics Statistics {
        get {
            ulong retiring = 0;
            foreach (var (Upload, Completion) in _retired) retiring = checked(retiring + Upload.Bytes);
            var deferred = 0;
            var limited = 0;
            foreach (var array in _arrays) {
                var missing = false;
                for (var layer = 0; layer < array.ResidentMips.Length; layer++) {
                    missing |= array.WantedMips[layer] < array.ResidentMips[layer];
                    if (array.DesiredMips[layer] > array.WantedMips[layer]) limited++;
                }
                if (missing) deferred++;
            }
            return new(Bytes, _peak, retiring, _uploaded, _stopped ? 0 : _pending.Sum(p => p.Bytes),
                _stopped ? 0 : _pending.Count, _installs, _evictions, _failures, deferred, Revision) {
                CpuUploadedBytes = _uploaded - _copied, CopiedBytes = _copied, BudgetLimitedLayers = limited
            };
        }
    }

    internal bool IsStopped => _stream is null || _stopped;
    internal bool IsStreaming => _stream is not null;

    private (uint Width, uint Height, int Levels, bool Srgb, PbrTextureSampler Sampler, int TailMip, bool Streamed) Shape(PbrTextureData t)
        => _stream is not null && _stream.TextureSources.TryGetValue(t, out var info)
            ? (info.Width, info.Height, info.Levels, t.Srgb, t.Sampler, info.TailMip, true)
            : (t.Width, t.Height, t.MipLevels.Length, t.Srgb, t.Sampler, 0, false);

    private static long TailBytes(uint width, uint height, int levels, int first)
    {
        long bytes = 0;
        for (var mip = first; mip < levels; mip++)
            bytes = checked(bytes + ((long)System.Math.Max(1u, width >> mip) * System.Math.Max(1u, height >> mip) * 4));
        return bytes;
    }

    private ArrayTexture UploadStreamTail(PbrTextureData[] sources)
    {
        var first = sources[0];
        var info = _stream!.TextureSources[first];
        var upload = new TextureArrayUpload(_frame, first.Width, first.Height, first.Srgb,
            [.. sources.Select(t => t.MipLevels.ToArray())], _budget - Bytes);
        try {
            while (!upload.Complete) upload.Advance(uint.MaxValue);
            var sampler = WGPUSamplerDescriptor.Default;
            sampler.AddressModeU = first.Sampler.AddressU; sampler.AddressModeV = first.Sampler.AddressV;
            sampler.MinFilter = first.Sampler.MinFilter; sampler.MagFilter = first.Sampler.MagFilter;
            sampler.MipmapFilter = first.Sampler.MipFilter;
            sampler.LodMaxClamp = sources.Length > 1 || first.Sampler.UseMipmaps ? info.Levels - 1 : 0;
            var result = new ArrayTexture(upload.Texture, upload.View, _gpu.Own(Wgpu.CreateSampler(_gpu.Device, sampler))) {
                Sources = sources, Width = info.Width, Height = info.Height, Levels = info.Levels,
                TailMip = info.TailMip, ResidentMip = info.TailMip,
                DesiredMip = info.TailMip, Residency = upload,
                ResidentMips = Enumerable.Repeat(info.TailMip, sources.Length).ToArray(),
                WantedMips = Enumerable.Repeat(info.TailMip, sources.Length).ToArray(),
                DesiredMips = Enumerable.Repeat(info.TailMip, sources.Length).ToArray(),
                Idle = new int[sources.Length], Priorities = new float[sources.Length]
            };
            while (result.MinimumMip < result.TailMip
                && (long)System.Math.Max(1u, result.Width >> result.MinimumMip) * System.Math.Max(1u, result.Height >> result.MinimumMip) * 4 > _streaming.DecodedBytes)
                result.MinimumMip++;
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
                Textures[_arrays.IndexOf(array)] = next.Texture;
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
            array.DesiredMip = array.TailMip;
            for (var layer = 0; layer < array.Sources.Length; layer++) {
                array.Idle[layer] = array.WantedMips[layer] > array.ResidentMips[layer]
                    ? System.Math.Min(_streaming.IdleFrames, array.Idle[layer] + 1) : 0;
                array.DesiredMips[layer] = array.WantedMips[layer] <= array.ResidentMips[layer] || array.Idle[layer] >= _streaming.IdleFrames
                    ? array.WantedMips[layer] : array.ResidentMips[layer];
                array.DesiredMips[layer] = System.Math.Max(array.MinimumMip, array.DesiredMips[layer]);
                array.DesiredMip = System.Math.Min(array.DesiredMip, array.DesiredMips[layer]);
            }
        }
        BudgetDemand();
        foreach (var pending in _pending) {
            for (var layer = 0; layer < pending.Mips.Length; layer++) {
                var old = pending.Array.ResidentMips[layer]; var wanted = pending.Array.DesiredMips[layer];
                if ((pending.Mips[layer] < old && wanted > pending.Mips[layer])
                    || (pending.Mips[layer] > old && wanted < pending.Mips[layer])) {
                    pending.Cancellation.Cancel(); break;
                }
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
            var available = _budget - Bytes - _pending.Where(p => p.Upload is null).Aggregate(0ul, (bytes, p) => bytes + AllocationBytes(p));
            var decodedAvailable = _streaming.DecodedBytes - _pending.Sum(p => p.Bytes);
            foreach (var array in _arrays) {
                var demand = DescribeDemand(array);
                if (array.Residency is null || !demand.Changed || array.RetryFrame > _clock
                    || _pending.Any(p => ReferenceEquals(p.Array, array)))
                    continue;
                var allocation = demand.Mip == array.ResidentMip ? 0 : checked(TailBytes(array.Width, array.Height, array.Levels, demand.Mip) * array.Sources.Length);
                if (demand.Bytes > decodedAvailable || (ulong)allocation > available) continue;
                if (best is null || CompareDemand(array, best) > 0) best = array;
            }
            if (best is null) break;
            StartRequest(best);
        }

        // Share this BeginFrame's allowance between already admitted ready arrays.
        // Completed uploads stay owned by their requests until the following BeginFrame.
        for (var attempt = 0; attempt < _pending.Count && _streaming.UploadBytesPerFrame - _uploaded >= 4; attempt++) {
            var available = _budget - Bytes;
            Request? active = null;
            foreach (var request in _pending) {
                if (!request.Read.IsCompletedSuccessfully || request.Cancellation.IsCancellationRequested
                    || request.Upload is { Complete: true }
                    || (request.Upload is null && AllocationBytes(request) > available)) continue;
                if (active is null || CompareLayer(request.Array, request.Layer, request.Bytes, active.Array, active.Layer, active.Bytes) > 0)
                    active = request;
            }
            if (active is null) break;
            if (active.Upload is null) {
                var width = System.Math.Max(1u, active.Array.Width >> active.Mip);
                var height = System.Math.Max(1u, active.Array.Height >> active.Mip);
                active.Upload = active.Mip == active.Array.ResidentMip
                    ? TextureArrayUpload.Update(_frame, width, height, active.Array.Texture, active.Array.View, active.Read.Result)
                    : new(_frame, width, height, active.Array.Sources[0].Srgb, active.Read.Result, available,
                        active.Array.Texture, active.Array.ResidentMip - active.Mip, active.Array.Levels - active.Mip);
            }
            var advanced = active.Upload.Advance(_streaming.UploadBytesPerFrame - _uploaded);
            _uploaded += advanced;
            _copied += active.Upload.CopiedBytes;
            _peak = System.Math.Max(_peak, Bytes);
            if (advanced == 0 && !active.Upload.Complete) break;
        }
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
        var aGap = aLayer < 0 ? 0 : a.ResidentMips[aLayer] - a.DesiredMips[aLayer];
        var bGap = bLayer < 0 ? 0 : b.ResidentMips[bLayer] - b.DesiredMips[bLayer];
        var gap = aGap.CompareTo(bGap);
        if (gap != 0) return gap;
        var aScore = aLayer < 0 ? 0 : (double)a.Priorities[aLayer] / System.Math.Max(1, aBytes);
        var bScore = bLayer < 0 ? 0 : (double)b.Priorities[bLayer] / System.Math.Max(1, bBytes);
        return aScore.CompareTo(bScore);
    }

    private void BudgetDemand()
    {
        var available = _budget - _gpu.Bytes;
        _budgetDrops.Clear(); _budgetHeadroom.Clear();
        ulong target = 0;
        ulong Size(ArrayTexture array, int mip) => checked((ulong)TailBytes(array.Width, array.Height, array.Levels, mip) * (ulong)array.Sources.Length);
        void Add(ArrayTexture array, int order) {
            if (array.DesiredMip >= array.TailMip) return;
            var coarser = Size(array, array.DesiredMip + 1);
            double pixels = 0;
            for (var layer = 0; layer < array.Sources.Length; layer++)
                if (array.DesiredMips[layer] == array.DesiredMip)
                    pixels = System.Math.Max(pixels, array.Priorities[layer]);
            var score = (Size(array, array.DesiredMip) - coarser) / (1 + pixels * pixels);
            _budgetDrops.Enqueue((array, order), (-score, order));
            _budgetHeadroom.Enqueue(array, -(long)coarser);
        }
        for (var order = 0; order < _arrays.Count; order++) {
            var array = _arrays[order]; if (array.Residency is null) continue;
            target = checked(target + Size(array, array.DesiredMip)); Add(array, order);
        }
        while (true) {
            ulong replacement = 0;
            while (_budgetHeadroom.TryPeek(out var array, out var priority)) {
                if (array.DesiredMip < array.TailMip && (ulong)(-priority) == Size(array, array.DesiredMip + 1)) {
                    replacement = (ulong)(-priority); break;
                }
                _budgetHeadroom.Dequeue();
            }
            // Keep space for the old coarser tail during one fine replacement.
            // All actual pending/retiring allocations are still admitted separately below.
            if (target <= available && replacement <= available - target) return;
            if (!_budgetDrops.TryDequeue(out var drop, out _)) return;
            var victim = drop.Array;
            var bytes = Size(victim, victim.DesiredMip);
            for (var layer = 0; layer < victim.Sources.Length; layer++)
                if (victim.DesiredMips[layer] == victim.DesiredMip) victim.DesiredMips[layer]++;
            victim.DesiredMip++;
            target -= bytes - Size(victim, victim.DesiredMip);
            Add(victim, drop.Order);
        }
    }

    private void StartRequest(ArrayTexture array)
    {
        var demand = DescribeDemand(array);
        var mip = demand.Mip; var bytes = demand.Bytes;
        var mips = array.ResidentMips.ToArray();
        if (demand.Layer >= 0) mips[demand.Layer]--;
        else for (var layer = 0; layer < mips.Length; layer++) mips[layer] = System.Math.Max(mip, mips[layer]);
        var cancellation = new CancellationTokenSource();
        var read = Task.Run(async () => {
            var result = new ReadOnlyMemory<byte>[array.Sources.Length][];
            for (var layer = 0; layer < result.Length; layer++) {
                result[layer] = new ReadOnlyMemory<byte>[array.Levels - mip];
                if (mips[layer] < array.ResidentMips[layer])
                    result[layer][mips[layer] - mip] = await _stream!.ReadTextureMipAsync(array.Sources[layer], mips[layer], cancellation.Token).ConfigureAwait(false);
            }
            return result;
        }, cancellation.Token);
        _pending.Add(new(array, mip, demand.Layer, mips, bytes, cancellation, read));
    }

    private static ulong AllocationBytes(Request request) => request.Mip == request.Array.ResidentMip ? 0
        : checked((ulong)TailBytes(request.Array.Width, request.Array.Height, request.Array.Levels, request.Mip) * (ulong)request.Array.Sources.Length);

    private static (int Mip, long Bytes, bool Changed, int Layer) DescribeDemand(ArrayTexture array)
    {
        if (array.DesiredMip > array.ResidentMip) return (array.DesiredMip, 0, true, -1);
        var selected = -1;
        var bestGap = 0; double bestScore = 0;
        for (var layer = 0; layer < array.Sources.Length; layer++) {
            var gap = array.ResidentMips[layer] - array.DesiredMips[layer];
            if (gap <= 0) continue;
            var next = array.ResidentMips[layer] - 1;
            var cost = (long)System.Math.Max(1u, array.Width >> next) * System.Math.Max(1u, array.Height >> next) * 4;
            var score = (double)array.Priorities[layer] / cost;
            if (selected < 0 || gap > bestGap || (gap == bestGap && score > bestScore)) {
                selected = layer; bestGap = gap; bestScore = score;
            }
        }
        if (selected < 0) return (array.ResidentMip, 0, false, -1);
        var nextMip = array.ResidentMips[selected] - 1;
        var bytes = checked((long)System.Math.Max(1u, array.Width >> nextMip) * System.Math.Max(1u, array.Height >> nextMip) * 4);
        return (System.Math.Min(array.ResidentMip, nextMip), bytes, true, selected);
    }

    private void UpdateMipClamps(ArrayTexture changed)
    {
        for (var material = 0; material < _materialUses.Length; material++) {
            var uses = _materialUses[material];
            if (!uses.Any(use => ReferenceEquals(use.Texture, changed))) continue;
            uint packed = 0;
            for (var map = 0; map < uses.Length; map++) {
                var (array, layer) = uses[map];
                if (array.Residency is null) continue;
                packed |= (uint)(array.ResidentMips[layer] - array.ResidentMip) << (map * 4);
                if (array.Sources.Length > 1 && !array.Sources[layer].Sampler.UseMipmaps) packed |= 1u << (20 + map);
            }
            var old = _parameters[material];
            _parameters[material] = old with { Indices = new(old.Indices.x, old.Indices.y, old.Indices.z, packed) };
            Wgpu.WriteBuffer<PbrMaterialGpu>(_gpu.Queue, Table.GetWgpu<WGPUBuffer>(),
                (ulong)material * (ulong)System.Runtime.InteropServices.Marshal.SizeOf<PbrMaterialGpu>(), _parameters.AsSpan(material, 1));
        }
    }

    private void RebuildGroups(ArrayTexture changed)
    {
        Span<WGPUBindGroupEntry> entries = stackalloc WGPUBindGroupEntry[12];
        for (var batch = 0; batch < Groups.Length; batch++) {
            var maps = _batchMaps[batch];
            if (!maps.Contains(changed)) continue;
            entries.Clear();
            entries[0] = GpuBinding.Buffer(0, Table); entries[1] = GpuBinding.Buffer(1, Uniforms[batch]);
            for (var map = 0; map < 5; map++) {
                entries[2 + (map * 2)] = GpuBinding.Texture((uint)(2 + (map * 2)), maps[map].View.GetWgpu<WGPUTextureView>());
                entries[3 + (map * 2)] = GpuBinding.Sampler((uint)(3 + (map * 2)), maps[map].Sampler);
            }
            var next = GpuBinding.Group(_gpu, _layout, entries);
            _gpu.Release(Groups[batch]);
            Groups[batch] = next;
        }
    }

    public ValueTask StopAsync() => new(_stop ??= DrainAsync());

    private async Task DrainAsync()
    {
        foreach (var pending in _pending) pending.Cancellation.Cancel();
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
