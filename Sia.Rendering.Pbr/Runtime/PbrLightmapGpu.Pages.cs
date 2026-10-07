using System.Numerics;
using Sia;
using Sia.Math;
using Sia.WebGPU;

namespace Sia.Engine.Rendering.Pbr;

public readonly record struct PbrLightmapStreamingStatistics(
    ulong AllocatedBytes, int PageCapacity, int ResidentPages, int RetiringPages,
    uint UploadedBytes, long ReservedDecodedBytes, int PendingRequests,
    int DeferredPages, long Installs, long Evictions, long Failures, ulong Revision);

internal sealed partial class PbrLightmapGpu
{
    internal const int UploadScratchBytes = PbrLightmapStream.PageSide * PbrLightmapStream.PageSide * 4;
    internal const int RequiredDecodedBytes = PbrLightmapStream.PageBytes + 16 + UploadScratchBytes;
    internal PbrLightmapStream? PagedSource { get; }
    private readonly PbrTextureStreamingSettings? _streaming;
    private readonly int _grid;
    private readonly int[] _pageAddress = [], _slotPages = [], _pageSlots = [], _instanceReceivers = [];
    private readonly long[] _retry = [];
    private readonly int[][] _receiverPages = [];
    private readonly int[] _receiverMaximumMips = [];
    private readonly float[] _demand = [];
    private readonly bool[] _wanted = [];
    private readonly uint[] _table = [];
    private readonly Task<bool>?[] _retiring = [];
    private readonly byte[] _uploadScratch = [];
    private readonly PriorityQueue<int, (float Score, int Tie)> _priorities = new();
    private readonly BoundedRequests<int, ReadOnlyMemory<byte>>? _requests;
    private Task? _stop;
    private bool _stopped;
    private Entity _metadataBuffer;
    private ulong _tableByteOffset;
    private long _clock, _installs, _evictions, _failures;
    private uint _uploaded;
    internal ulong Revision { get; private set; }
    internal bool IsStopped => PagedSource is null || _stopped;

    public PbrLightmapStreamingStatistics Statistics => new(Bytes, _slotPages.Length,
        _slotPages.Count(p => p >= 0), _slotPages.Count(p => p == -2), _uploaded,
        PagedSource is null || _stopped ? 0 : UploadScratchBytes + _requests!.ReservedBytes,
        _stopped ? 0 : _requests?.Count ?? 0, _wanted.Where((w, i) => w && _pageSlots[i] < 0).Count(),
        _installs, _evictions, _failures, Revision);

    internal static int ChartMaximumMip(int resolution, PbrLightmapChart chart)
        => BitOperations.TrailingZeroCount((uint)(resolution | chart.X | chart.Y | chart.Width | chart.Height));

    internal static int[] ReceiverPageBases(PbrLightmapStream stream)
    {
        var caps = PbrLightmapStream.ReceiverMaximumMips(stream.Resolution, stream.Receivers.ToArray(), stream.Charts.ToArray());
        var bases = new int[caps.Length + 1];
        for (var i = 0; i < caps.Length; i++) {
            bases[i + 1] = bases[i];
            for (var mip = 0; mip <= caps[i]; mip++) {
                var side = ((stream.Receivers.Span[i].Resolution >> mip) + 31) / 32;
                bases[i + 1] = checked(bases[i + 1] + side * side);
            }
        }
        return bases;
    }

    internal static int TableMetadataOffset(PbrLightmapStream stream)
        => 1 + stream.Receivers.Length * 5 + stream.Charts.Length * 2;

    internal static float4[] CreateMetadata(PbrLightmapStream stream)
    {
        var receivers = stream.Receivers.ToArray(); var charts = stream.Charts.ToArray();
        var bases = ReceiverPageBases(stream);
        var caps = PbrLightmapStream.ReceiverMaximumMips(stream.Resolution, receivers, charts);
        var metadata = new float4[TableMetadataOffset(stream) + (bases[^1] + 3) / 4];
        metadata[0] = new(receivers.Length, charts.Length, caps.Max() + 1, stream.Resolution);
        stream.DecodeScales.Span.CopyTo(metadata.AsSpan(1));
        var indices = receivers.Select((r, i) => (r.StaticInstance, i)).ToDictionary(r => r.StaticInstance, r => r.i);
        var chartBase = 1 + receivers.Length * 4;
        for (var c = 0; c < charts.Length; c++) {
            var chart = charts[c];
            metadata[chartBase + c] = new(
                BitConverter.UInt32BitsToSingle((uint)chart.X | (uint)chart.Y << 16),
                BitConverter.UInt32BitsToSingle((uint)chart.Width | (uint)chart.Height << 16),
                ChartMaximumMip(stream.Resolution, chart), indices[chart.StaticInstance]);
            float Band(int band) => BitConverter.UInt32BitsToSingle(System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
                stream.CoarseCoefficients.Span.Slice(c * 16 + band * 4, 4)));
            metadata[chartBase + charts.Length + c] = new(Band(0), Band(1), Band(2), Band(3));
        }
        for (var r = 0; r < receivers.Length; r++) {
            var receiver = receivers[r];
            metadata[chartBase + charts.Length * 2 + r] = new(
                BitConverter.UInt32BitsToSingle((uint)receiver.X | (uint)receiver.Y << 16), receiver.Resolution, bases[r], caps[r]);
        }
        return metadata;
    }

    internal PbrLightmapGpu(in GpuFrame frame, PbrLightmapStream stream, PbrSceneAsset scene,
        ulong maximumBytes, PbrTextureStreamingSettings settings, PbrSceneStream? geometryStream = null)
    {
        if (settings.UploadBytesPerFrame < PbrLightmapStream.PageBytes + 32 || settings.DecodedBytes < RequiredDecodedBytes
            || settings.IdleFrames < 0 || !float.IsFinite(settings.MipBias) || settings.MipBias < 0)
            throw new ArgumentException("Lightmap streaming requires a whole page upload and independently budgeted decode/staging.");
        PagedSource = stream; _streaming = settings;
        _gpu = new(frame, maximumBytes);
        try {
            var gridBudget = (int)System.Math.Min(8192.0, System.Math.Floor(System.Math.Sqrt(maximumBytes / (double)PbrLightmapStream.PageBytes)));
            _grid = System.Math.Min(System.Math.Min(gridBudget, (int)_gpu.Limits.MaxTextureDimension2D / PbrLightmapStream.PageSide),
                (int)System.Math.Ceiling(System.Math.Sqrt(stream.Pages.Length)));
            if (_grid < 1 || _gpu.Limits.MaxTextureArrayLayers < 4)
                throw new NotSupportedException("Lightmap page pool cannot fit its byte or texture limits.");
            _slotPages = Enumerable.Repeat(-1, _grid * _grid).ToArray();
            _retiring = new Task<bool>?[_slotPages.Length];
            _pageSlots = Enumerable.Repeat(-1, stream.Pages.Length).ToArray();
            _retry = new long[stream.Pages.Length]; _demand = new float[stream.Pages.Length]; _wanted = new bool[stream.Pages.Length];
            // Reserve each decoded payload before starting IO. One shared row scratch is reused by uploads.
            // Limit the window to one frame's upload budget and a small fixed ceiling, even with huge user budgets.
            var requestCapacity = (int)System.Math.Min(32, System.Math.Min(_slotPages.Length,
                System.Math.Min((settings.DecodedBytes - UploadScratchBytes) / (PbrLightmapStream.PageBytes + 16),
                    settings.UploadBytesPerFrame / (PbrLightmapStream.PageBytes + 32))));
            _requests = new(stream.ReadPageAsync, requestCapacity, settings.DecodedBytes - UploadScratchBytes);
            var bases = ReceiverPageBases(stream); _table = new uint[((bases[^1] + 3) / 4) * 4];
            _pageAddress = new int[stream.Pages.Length];
            var receiverGroups = stream.Pages.ToArray().Select((p, index) => (p.Receiver, index))
                .GroupBy(p => p.Receiver).ToDictionary(g => g.Key, g => g.Select(p => p.index).ToArray());
            _receiverPages = Enumerable.Range(0, stream.Receivers.Length).Select(r => receiverGroups[r]).ToArray();
            _receiverMaximumMips = PbrLightmapStream.ReceiverMaximumMips(stream.Resolution, stream.Receivers.ToArray(), stream.Charts.ToArray());
            for (var p = 0; p < stream.Pages.Length; p++) {
                var page = stream.Pages.Span[p]; var size = stream.Receivers.Span[page.Receiver].Resolution;
                var at = bases[page.Receiver];
                for (var mip = 0; mip < page.Mip; mip++) { var side = ((size >> mip) + 31) / 32; at += side * side; }
                _pageAddress[p] = at + page.Y * (((size >> page.Mip) + 31) / 32) + page.X;
            }
            _instanceReceivers = Enumerable.Repeat(-1, geometryStream?.SourceInstanceCount ?? scene.Instances.Length).ToArray();
            var receiverIds = stream.Receivers.ToArray().Select((r, i) => (r.StaticInstance, i)).ToDictionary(r => r.StaticInstance, r => r.i);
            var staticIndex = 0;
            for (var i = 0; i < _instanceReceivers.Length; i++) {
                if (geometryStream is not null) {
                    if (receiverIds.TryGetValue(geometryStream.StaticSourceInstances.Span[i], out var receiver)) _instanceReceivers[i] = receiver;
                    continue;
                }
                if (scene.Instances.Span[i].Dynamic) continue;
                if (receiverIds.TryGetValue(staticIndex++, out var r)) _instanceReceivers[i] = r;
            }
            _uploadScratch = new byte[UploadScratchBytes];
            var descriptor = WGPUTextureDescriptor.Default;
            descriptor.Dimension = WGPUTextureDimension._2D;
            descriptor.Size = new() { Width = (uint)(_grid * PbrLightmapStream.PageSide), Height = (uint)(_grid * PbrLightmapStream.PageSide), DepthOrArrayLayers = 4 };
            descriptor.Format = WGPUTextureFormat.RGBA8Unorm; descriptor.MipLevelCount = 1;
            descriptor.Usage = WGPUTextureUsage.TextureBinding | WGPUTextureUsage.CopyDst;
            Texture = _gpu.Texture(descriptor, (ulong)_slotPages.Length * PbrLightmapStream.PageBytes);
            var view = WGPUTextureViewDescriptor.Default; view.Dimension = WGPUTextureViewDimension._2DArray;
            View = _gpu.Own(Wgpu.CreateTextureView(Texture.GetWgpu<WGPUTexture>(), view));
        }
        catch { _stopped = true; _gpu.Dispose(); throw; }
    }

    internal void BindMetadata(PbrGpuScene scene)
    {
        if (PagedSource is null) return;
        _metadataBuffer = scene.Vertices;
        _tableByteOffset = scene.GeometryVertexBytes + scene.LightmapMetadataPrefixBytes + (ulong)TableMetadataOffset(PagedSource) * 16;
    }

    public void BeginFrame()
    {
        if (PagedSource is null) return;
        if (_stop is not null) throw new ObjectDisposedException(nameof(PbrLightmapGpu));
        _clock++; _uploaded = 0;
        for (var slot = 0; slot < _retiring.Length; slot++) {
            if (_retiring[slot] is not { IsCompleted: true } completion) continue;
            if (!completion.GetAwaiter().GetResult()) throw new InvalidOperationException("Lightmap page retirement failed.");
            _retiring[slot] = null; _slotPages[slot] = -1;
        }
        for (var request = 0; request < _requests!.Count;) {
            var pending = _requests.Requests[request];
            if (!pending.Read.IsCompleted) { request++; continue; }
            if (pending.IsCancellationRequested || !pending.Read.IsCompletedSuccessfully || !_wanted[pending.Key]) {
                if (!pending.Read.IsCompletedSuccessfully && !pending.IsCancellationRequested) {
                    _ = pending.Read.Exception; _failures++; _retry[pending.Key] = _clock + System.Math.Max(1, _streaming!.IdleFrames);
                }
                _requests.Release(pending.Key);
            } else {
                if (_uploaded + PbrLightmapStream.PageBytes + 32 > _streaming!.UploadBytesPerFrame) { request++; continue; }
                var free = Array.IndexOf(_slotPages, -1);
                if (free < 0) {
                    // Do not retire more slots than completed wanted requests can use.
                    var waiting = _requests.Requests.Count(p => p.Read.IsCompletedSuccessfully
                        && !p.IsCancellationRequested && _wanted[p.Key]);
                    var evict = _retiring.Count(t => t is not null) < waiting
                        ? Array.FindIndex(_slotPages, p => p >= 0 && !_wanted[p]) : -1;
                    if (evict >= 0) {
                        var old = _slotPages[evict]; WriteMapping(old, 0); _pageSlots[old] = -1; _slotPages[evict] = -2;
                        _retiring[evict] = GpuQueueCompletion.AfterSubmittedWork(_gpu.Queue); _evictions++; Revision++;
                    }
                    request++;
                } else {
                    UploadPage(free, pending.Read.GetAwaiter().GetResult().Span);
                    WriteMapping(pending.Key, (uint)free + 1); _pageSlots[pending.Key] = free; _slotPages[free] = pending.Key;
                    _installs++; Revision++; _requests.Release(pending.Key);
                }
            }
        }
        Array.Clear(_demand);
    }

    internal void Demand(uint instance, float pixels)
    {
        if (PagedSource is null || pixels <= 0 || instance >= _instanceReceivers.Length || _instanceReceivers[instance] < 0) return;
        var r = _instanceReceivers[instance]; var receiver = PagedSource.Receivers.Span[r];
        pixels = float.IsFinite(pixels) ? pixels : receiver.Resolution;
        var wantedMip = System.Math.Clamp(MathF.Log2(receiver.Resolution / System.Math.Max(1, pixels)) - _streaming!.MipBias, 0, _receiverMaximumMips[r]);
        foreach (var p in _receiverPages[r]) {
            var page = PagedSource.Pages.Span[p]; var side = ((receiver.Resolution >> page.Mip) + 31) / 32;
            var score = pixels * pixels / (side * side * (1 + MathF.Abs(page.Mip - wantedMip)));
            _demand[p] = System.Math.Max(_demand[p], System.Math.Min(score, float.MaxValue));
        }
    }

    public void EndFrame()
    {
        if (PagedSource is null || _stop is not null) return;
        SelectWantedPages(_demand, _wanted, _slotPages.Length, _priorities);
        foreach (var pending in _requests!.Requests) if (!_wanted[pending.Key]) _requests.Cancel(pending.Key);
        while (_requests.Count < _requests.Capacity) {
            var best = -1;
            for (var p = 0; p < _wanted.Length; p++)
                if (_wanted[p] && !_requests.Contains(p) && _pageSlots[p] < 0 && _retry[p] <= _clock
                    && (best < 0 || _demand[p] > _demand[best])) best = p;
            if (best < 0) break;
            if (!_requests.TryStart(best, PbrLightmapStream.PageBytes + 16)) break;
        }
    }

    internal static void SelectWantedPages(ReadOnlySpan<float> demand, Span<bool> wanted, int capacity,
        PriorityQueue<int, (float Score, int Tie)> priorities)
    {
        if (capacity < 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (demand.Length != wanted.Length) throw new ArgumentException("Page demand and selection must have the same length.", nameof(wanted));
        priorities.Clear(); wanted.Clear();
        if (capacity == 0) return;
        for (var p = 0; p < demand.Length; p++) {
            if (demand[p] <= 0) continue;
            var priority = (demand[p], -p);
            if (priorities.Count < capacity) priorities.Enqueue(p, priority);
            // Reject lower-priority pages without inserting and removing them again.
            else priorities.EnqueueDequeue(p, priority);
        }
        foreach (var item in priorities.UnorderedItems) wanted[item.Element] = true;
    }

    private void WriteMapping(int page, uint slot)
    {
        var at = _pageAddress[page]; _table[at] = slot; var first = at / 4 * 4;
        Wgpu.WriteBuffer<uint>(_gpu.Queue, _metadataBuffer.GetWgpu<WGPUBuffer>(), _tableByteOffset + (ulong)first * 4, _table.AsSpan(first, 4));
        _uploaded += 16;
    }

    private unsafe void UploadPage(int slot, ReadOnlySpan<byte> bytes)
    {
        for (var band = 0; band < 4; band++) {
            for (var pixel = 0; pixel < PbrLightmapStream.PageSide * PbrLightmapStream.PageSide; pixel++)
                bytes.Slice(pixel * 16 + band * 4, 4).CopyTo(_uploadScratch.AsSpan(pixel * 4, 4));
            var target = new WGPUTexelCopyTextureInfo { Texture = (WGPUTexture*)Texture.GetWgpu<WGPUTexture>().DangerousGetHandle(),
                Aspect = WGPUTextureAspect.All, Origin = new() { X = (uint)(slot % _grid * PbrLightmapStream.PageSide),
                    Y = (uint)(slot / _grid * PbrLightmapStream.PageSide), Z = (uint)band } };
            var layout = new WGPUTexelCopyBufferLayout { BytesPerRow = PbrLightmapStream.PageSide * 4, RowsPerImage = PbrLightmapStream.PageSide };
            var size = new WGPUExtent3D { Width = PbrLightmapStream.PageSide, Height = PbrLightmapStream.PageSide, DepthOrArrayLayers = 1 };
            fixed (byte* data = _uploadScratch)
                WgpuUnsafe.wgpuQueueWriteTexture((WGPUQueue*)_gpu.Queue.DangerousGetHandle(), &target, data, UploadScratchBytes, &layout, &size);
        }
        _uploaded += PbrLightmapStream.PageBytes;
    }

    public ValueTask StopAsync() => new(_stop ??= DrainAsync());
    private async Task DrainAsync()
    {
        if (_requests is not null) await _requests.DisposeAsync().ConfigureAwait(false);
        foreach (var task in _retiring) if (task is not null && !await task.ConfigureAwait(false))
            throw new InvalidOperationException("Lightmap page retirement failed at shutdown.");
        _stopped = true;
    }
}
