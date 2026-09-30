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
    ulong Revision);

internal sealed partial class PbrMaterials
{
    private sealed class Request(
        ArrayTexture array,
        int mip,
        long bytes,
        CancellationTokenSource cancellation,
        Task<ReadOnlyMemory<byte>[][]> read)
    {
        public readonly ArrayTexture Array = array;
        public readonly int Mip = mip;
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
    private readonly ArrayTexture[][] _materialArrays;
    private readonly List<ArrayTexture[]> _batchMaps;
    private Request? _pending;
    private long _clock;
    private long _installs, _evictions, _failures;
    private uint _uploaded;
    private ulong _peak;
    private Task? _stop;
    private bool _stopped;

    public ulong Revision { get; private set; }

    public PbrTextureStreamingStatistics Statistics {
        get {
            ulong retiring = 0;
            foreach (var (Upload, Completion) in _retired) retiring = checked(retiring + Upload.Bytes);
            var deferred = 0;
            foreach (var array in _arrays)
                if (array.WantedMip < array.ResidentMip) deferred++;
            return new(Bytes, _peak, retiring, _uploaded, _stopped ? 0 : _pending?.Bytes ?? 0,
                _stopped || _pending is null ? 0 : 1, _installs, _evictions, _failures, deferred, Revision);
        }
    }

    internal bool IsStopped => _stream is null || _stopped;
    internal bool IsStreaming => _stream is not null;

    private (uint Width, uint Height, int Levels, bool Srgb, PbrTextureSampler Sampler) Shape(PbrTextureData t)
        => _stream is not null && _stream.TextureSources.TryGetValue(t, out var info)
            ? (info.Width, info.Height, info.Levels, t.Srgb, t.Sampler)
            : (t.Width, t.Height, t.MipLevels.Length, t.Srgb, t.Sampler);

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
            sampler.LodMaxClamp = first.Sampler.UseMipmaps ? info.Levels - 1 : 0;
            var result = new ArrayTexture(upload.Texture, upload.View, _gpu.Own(Wgpu.CreateSampler(_gpu.Device, sampler))) {
                Sources = sources, Width = info.Width, Height = info.Height, Levels = info.Levels,
                TailMip = info.TailMip, ResidentMip = info.TailMip, WantedMip = info.TailMip,
                DesiredMip = info.TailMip, Residency = upload
            };
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
        _uploaded = 0;
        for (var i = _retired.Count - 1; i >= 0; i--) {
            if (!_retired[i].Completion.IsCompleted) continue;
            if (!_retired[i].Completion.GetAwaiter().GetResult())
                throw new InvalidOperationException("GPU queue retirement failed; texture storage cannot be reclaimed safely.");
            _retired[i].Upload.Dispose();
            _retired.RemoveAt(i);
        }
        if (_pending?.Upload is { Complete: true } next && !_pending.Cancellation.IsCancellationRequested) {
            var array = _pending.Array;
            _retired.Add((array.Residency!, GpuQueueCompletion.AfterSubmittedWork(_gpu.Queue)));
            if (_pending.Mip > array.ResidentMip) _evictions++;
            array.ResidentMip = _pending.Mip;
            array.Texture = next.Texture; array.View = next.View; array.Residency = next;
            Textures[_arrays.IndexOf(array)] = next.Texture;
            _installs++;
            Revision++;
            RebuildGroups(array);
            _pending.Cancellation.Dispose();
            _pending = null;
        }
        foreach (var array in _arrays) { array.WantedMip = array.TailMip; array.Priority = 0; }
        _peak = System.Math.Max(_peak, Bytes);
    }

    public void Demand(int material, float pixels)
    {
        if (_stream is null || pixels <= 0) return;
        foreach (var array in _materialArrays[material]) {
            if (array.Residency is null) continue;
            var ratio = System.Math.Max(array.Width, array.Height) / System.Math.Max(1f, pixels);
            var mip = float.IsFinite(ratio) ? (int)MathF.Floor(MathF.Log2(ratio) - _streaming.MipBias) : 0;
            array.WantedMip = System.Math.Min(array.WantedMip, System.Math.Clamp(mip, 0, array.TailMip));
            array.Priority = System.Math.Max(array.Priority, pixels);
        }
    }

    public void EndFrame()
    {
        if (_stream is null || _stop is not null) return;
        foreach (var array in _arrays) {
            if (array.WantedMip > array.ResidentMip) array.CoarserFrames++;
            else array.CoarserFrames = 0;
            array.DesiredMip = array.WantedMip <= array.ResidentMip || array.CoarserFrames >= _streaming.IdleFrames
                ? array.WantedMip : array.ResidentMip;
        }
        if (_pending is { } pending && pending.Array.DesiredMip > pending.Mip) pending.Cancellation.Cancel();
        if (_pending is { } cancelled && cancelled.Cancellation.IsCancellationRequested) {
            if (!cancelled.Read.IsCompleted) return;
            _ = cancelled.Read.Exception;
            if (cancelled.Upload is not null) {
                cancelled.Upload.Abandon();
                _retired.Add((cancelled.Upload, GpuQueueCompletion.AfterSubmittedWork(_gpu.Queue)));
            }
            cancelled.Cancellation.Dispose(); _pending = null;
        }
        if (_pending is { } failed && failed.Read.IsCompleted && !failed.Read.IsCompletedSuccessfully) {
            _ = failed.Read.Exception;
            _failures++;
            failed.Array.RetryFrame = _clock + 60;
            failed.Cancellation.Dispose(); _pending = null;
        }
        if (_pending is null) {
            ArrayTexture? best = null;
            var available = _budget - Bytes;
            foreach (var array in _arrays) {
                if (array.Residency is null || array.DesiredMip == array.ResidentMip || array.RetryFrame > _clock)
                    continue;
                var mip = array.DesiredMip > array.ResidentMip ? array.DesiredMip : array.ResidentMip - 1;
                var bytes = checked(TailBytes(array.Width, array.Height, array.Levels, mip) * array.Sources.Length);
                if (bytes > _streaming.DecodedBytes || (ulong)bytes > available) continue;
                if (best is null || CompareDemand(array, best) > 0) best = array;
            }
            if (best is not null) StartRequest(best);
        }

        if (_pending is not { } active || !active.Read.IsCompletedSuccessfully) return;
        if (active.Upload is null) {
            if ((ulong)active.Bytes > _budget - Bytes) return;
            active.Upload = new(_frame, System.Math.Max(1u, active.Array.Width >> active.Mip),
                System.Math.Max(1u, active.Array.Height >> active.Mip), active.Array.Sources[0].Srgb, active.Read.Result, _budget - Bytes);
        }
        _uploaded = active.Upload.Advance(_streaming.UploadBytesPerFrame);
        _peak = System.Math.Max(_peak, Bytes);
    }

    private static int CompareDemand(ArrayTexture a, ArrayTexture b)
    {
        var coarser = (a.DesiredMip > a.ResidentMip).CompareTo(b.DesiredMip > b.ResidentMip);
        if (coarser != 0) return coarser;
        var gap = (a.ResidentMip - a.DesiredMip).CompareTo(b.ResidentMip - b.DesiredMip);
        return gap != 0 ? gap : a.Priority.CompareTo(b.Priority);
    }

    private void StartRequest(ArrayTexture array)
    {
        var mip = array.DesiredMip > array.ResidentMip ? array.DesiredMip : array.ResidentMip - 1;
        var bytes = checked(TailBytes(array.Width, array.Height, array.Levels, mip) * array.Sources.Length);
        var cancellation = new CancellationTokenSource();
        var read = Task.Run(async () => {
            var result = new ReadOnlyMemory<byte>[array.Sources.Length][];
            for (var layer = 0; layer < result.Length; layer++)
                result[layer] = await _stream!.ReadTextureTailAsync(array.Sources[layer], mip, cancellation.Token).ConfigureAwait(false);
            return result;
        }, cancellation.Token);
        _pending = new(array, mip, bytes, cancellation, read);
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
        if (_pending is { } pending) {
            pending.Cancellation.Cancel();
            try { await pending.Read.ConfigureAwait(false); }
            catch (Exception) { _ = pending.Read.Exception; }
            pending.Upload?.Abandon();
            pending.Read = Task.FromResult<ReadOnlyMemory<byte>[][]>([]);
            pending.Cancellation.Dispose();
        }
        _stopped = true;
    }
}
