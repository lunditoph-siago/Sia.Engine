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
    private readonly GpuFrame _frame;
    private readonly Entity _layout;
    private readonly ulong _budget;
    private readonly PbrSceneStream? _stream;
    private readonly PbrTextureStreamingSettings _streaming;
    private readonly List<ArrayTexture> _arrays = [];
    private readonly List<(TextureArrayUpload Upload, Task<bool> Completion)> _retired = [];
    private readonly ArrayTexture[][] _materialArrays;
    private readonly List<ArrayTexture[]> _batchMaps;
    private readonly BoundedRequests<(ArrayTexture Array, int Mip), ReadOnlyMemory<byte>[][]> _requests;
    private TextureArrayUpload? _upload;
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
            return new(Bytes, _peak, retiring, _uploaded, _requests.ReservedBytes,
                _requests.Count, _installs, _evictions, _failures, deferred, Revision);
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
            var result = new ArrayTexture(upload.Texture, upload.View, CreateSampler(first.Sampler, info.Levels)) {
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
        if (_upload is { Complete: true } next && Pending is { IsCancellationRequested: false } request) {
            var (array, mip) = request.Key;
            _retired.Add((array.Residency!, GpuQueueCompletion.AfterSubmittedWork(_gpu.Queue)));
            if (mip > array.ResidentMip) _evictions++;
            array.ResidentMip = mip;
            array.Texture = next.Texture; array.View = next.View; array.Residency = next;
            Textures[_arrays.IndexOf(array)] = next.Texture;
            _installs++;
            Revision++;
            RebuildGroups(array);
            _requests.Release(request.Key);
            _upload = null;
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
        if (Pending is { } pending && pending.Key.Array.DesiredMip > pending.Key.Mip) _requests.Cancel(pending.Key);
        if (Pending is { IsCancellationRequested: true } cancelled) {
            if (!cancelled.Read.IsCompleted) return;
            _ = cancelled.Read.Exception;
            if (_upload is not null) {
                _upload.Abandon();
                _retired.Add((_upload, GpuQueueCompletion.AfterSubmittedWork(_gpu.Queue)));
                _upload = null;
            }
            _requests.Release(cancelled.Key);
        }
        if (Pending is { } failed && failed.Read.IsCompleted && !failed.Read.IsCompletedSuccessfully) {
            _ = failed.Read.Exception;
            _failures++;
            failed.Key.Array.RetryFrame = _clock + 60;
            _requests.Release(failed.Key);
        }
        if (Pending is null) {
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

        if (Pending is not { } active || !active.Read.IsCompletedSuccessfully) return;
        if (_upload is null) {
            if ((ulong)active.ReservedBytes > _budget - Bytes) return;
            var (array, mip) = active.Key;
            _upload = new(_frame, System.Math.Max(1u, array.Width >> mip),
                System.Math.Max(1u, array.Height >> mip), array.Sources[0].Srgb, active.Read.Result, _budget - Bytes);
        }
        _uploaded = _upload.Advance(_streaming.UploadBytesPerFrame);
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
        if (!_requests.TryStart((array, mip), bytes))
            throw new InvalidOperationException("Selected material read cannot fit its request reservation.");
    }

    private BoundedRequests<(ArrayTexture Array, int Mip), ReadOnlyMemory<byte>[][]>.Request? Pending =>
        _requests.Count == 0 ? null : _requests.Requests[0];

    private Task<ReadOnlyMemory<byte>[][]> ReadTextureAsync((ArrayTexture Array, int Mip) key, CancellationToken token) =>
        Task.Run(async () => {
            var result = new ReadOnlyMemory<byte>[key.Array.Sources.Length][];
            for (var layer = 0; layer < result.Length; layer++)
                result[layer] = await _stream!.ReadTextureTailAsync(key.Array.Sources[layer], key.Mip, token).ConfigureAwait(false);
            return result;
        }, token);

    private void RebuildGroups(ArrayTexture changed)
    {
        for (var batch = 0; batch < Groups.Length; batch++) {
            var maps = _batchMaps[batch];
            if (!maps.Contains(changed)) continue;
            var next = CreateGroup(batch);
            _gpu.Release(Groups[batch]);
            Groups[batch] = next;
        }
    }

    public ValueTask StopAsync() => new(_stop ??= DrainAsync());

    private async Task DrainAsync()
    {
        await _requests.DisposeAsync().ConfigureAwait(false);
        _upload?.Abandon();
        _stopped = true;
    }
}
