using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using Sia;
using Sia.Math;
using Sia.WebGPU;

namespace Sia.Engine.Rendering.Pbr;

internal sealed class PbrGpuSelection : IDisposable
{
    private sealed class Slot(Entity buffer)
    {
        public readonly Entity Buffer = buffer;
        public Task? Mapping;
        public bool Recorded;
        public long Frame;
    }

    private readonly GpuResources _gpu;
    private readonly PbrGpuHierarchy _hierarchy;
    private readonly Entity[] _configuration = new Entity[8];
    private readonly Entity[] _groups = new Entity[8];
    private readonly Entity[] _expansionGroups = new Entity[8];
    private readonly Entity[] _rasterGroups = new Entity[8];
    private readonly Slot[] _slots = new Slot[3];
    private readonly uint[] _latest;
    private readonly List<int> _activeFeedback = [];
    private long _clock;
    private long _latestFrame = -1;
    private int _capture = -1;
    private bool _disposed;

    public Entity Work { get; }
    public Entity Arguments { get; }
    public Entity DispatchArguments { get; }
    public Entity Feedback { get; }
    public Entity CaptureBuffer => _capture < 0 ? default : _slots[_capture].Buffer;

    public ulong Bytes => _gpu.Bytes;
    public uint VisibleTriangles { get; private set; }
    public uint DeferredRefinements { get; private set; }
    public long FeedbackDropped { get; private set; }
    public long FeedbackFailures { get; private set; }
    public int PendingReadbacks {
        get {
            var count = 0;
            foreach (var slot in _slots)
                if (slot.Recorded || slot.Mapping is not null) count++;
            return count;
        }
    }

    public long FeedbackAge => _latestFrame < 0 ? -1 : _clock - _latestFrame;

    internal ReadOnlySpan<Entity> Configurations => _configuration;

    private ulong FeedbackBytes => (ulong)_hierarchy.PageCount * 8;
    private ulong ReadbackBytes => FeedbackBytes + 64;

    public PbrGpuSelection(in GpuFrame frame, PbrGpuHierarchy hierarchy, Entity rasterLayout, ulong budget)
    {
        _gpu = new(frame, budget);
        _hierarchy = hierarchy;
        _latest = new uint[checked(((int)hierarchy.PageCount * 2) + 16)];
        try {
            // Selected nodes and two traversal frontiers occupy disjoint buffer tails.
            // Sharing the binding keeps the compute storage-buffer count unchanged.
            var workRecords = checked((ulong)hierarchy.SingleCapacity + hierarchy.DoubleCapacity
                + ((ulong)hierarchy.NodeCapacity * 3));
            Work = _gpu.Buffer(checked(workRecords * (ulong)Unsafe.SizeOf<uint4>()),
                WGPUBufferUsage.Storage | WGPUBufferUsage.CopySrc);
            Arguments = _gpu.Buffer(80, WGPUBufferUsage.Storage | WGPUBufferUsage.Indirect | WGPUBufferUsage.CopySrc);
            // A writable counter buffer cannot also supply this dispatch's indirect arguments.
            DispatchArguments = _gpu.Buffer(16, WGPUBufferUsage.Indirect | WGPUBufferUsage.CopyDst);
            Feedback = _gpu.Buffer(FeedbackBytes, WGPUBufferUsage.Storage | WGPUBufferUsage.CopySrc);
            for (var i = 0; i < _groups.Length; i++) {
                _configuration[i] = _gpu.Buffer((ulong)Marshal.SizeOf<PbrGpuHierarchy.Configuration>(),
                    WGPUBufferUsage.Uniform | WGPUBufferUsage.CopyDst);
                _rasterGroups[i] = GpuBinding.Group(_gpu, rasterLayout, [
                    GpuBinding.Buffer(12, Work), GpuBinding.Buffer(13, _configuration[i])
                ]);
                _groups[i] = GpuBinding.Group(_gpu, hierarchy.Layout, [
                    GpuBinding.Buffer(0, _configuration[i]), GpuBinding.Buffer(1, hierarchy.Nodes),
                    GpuBinding.Buffer(2, hierarchy.Parts), GpuBinding.Buffer(3, hierarchy.Residency),
                    GpuBinding.Buffer(4, Work), GpuBinding.Buffer(5, Arguments), GpuBinding.Buffer(6, Feedback),
                    GpuBinding.Buffer(7, hierarchy.Instances)
                ]);
                _expansionGroups[i] = GpuBinding.Group(_gpu, hierarchy.ExpansionLayout, [
                    GpuBinding.Buffer(0, _configuration[i]), GpuBinding.Buffer(1, hierarchy.Nodes),
                    GpuBinding.Buffer(2, hierarchy.Parts), GpuBinding.Buffer(3, hierarchy.Residency),
                    GpuBinding.Buffer(4, Work), GpuBinding.Buffer(5, Arguments)
                ]);
            }
            for (var i = 0; i < _slots.Length; i++)
                _slots[i] = new(_gpu.Buffer(ReadbackBytes, WGPUBufferUsage.CopyDst | WGPUBufferUsage.MapRead));
        }
        catch {
            _gpu.Dispose();
            throw;
        }
    }

    public Entity RasterGroup(int view) => _rasterGroups[view];

    internal Entity Readback(int slot) => _slots[slot].Buffer;

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Prepare(PbrStreamResidency residency)
    {
        _clock++;
        _capture = -1;
        foreach (var slot in _slots) {
            if (slot.Mapping is { IsCompleted: true } mapping) {
                try {
                    mapping.GetAwaiter().GetResult();
                    if (slot.Frame > _latestFrame) {
                        Wgpu.GetMappedRangeReadOnly<uint>(slot.Buffer.GetWgpu<WGPUBuffer>(), 0, _latest.Length).CopyTo(_latest);
                        _activeFeedback.Clear();
                        for (var i = 0; i < (int)_hierarchy.PageCount; i++)
                            if ((_latest[i * 2] | _latest[(i * 2) + 1]) != 0) _activeFeedback.Add(i);
                        _latestFrame = slot.Frame;
                        var offset = (int)_hierarchy.PageCount * 2;
                        VisibleTriangles = _latest[offset + 15];
                        DeferredRefinements = checked(_latest[offset + 9] + _latest[offset + 11]);
                    }
                }
                catch (Exception) { FeedbackFailures++; }
                finally { Wgpu.UnmapBuffer(slot.Buffer.GetWgpu<WGPUBuffer>()); slot.Mapping = null; }
                if (_latest[((int)_hierarchy.PageCount * 2) + 10] != 0)
                    throw new InvalidOperationException("GPU hierarchy work capacity invariant failed.");
            }
            // The previous frame has submitted this copy before its next Prepare.
            // Mapping in the copy encoder would mark it pending before submission.
            if (slot.Recorded) {
                slot.Mapping = Wgpu.MapBufferReadAsync(slot.Buffer.GetWgpu<WGPUBuffer>(), 0, ReadbackBytes);
                slot.Recorded = false;
            }
        }
        if (_latestFrame >= 0 && _clock - _latestFrame <= 8)
            residency.ApplyGpuFeedback(_latest.AsSpan(0, (int)_hierarchy.PageCount * 2), CollectionsMarshal.AsSpan(_activeFeedback));
        for (var i = 0; i < _slots.Length; i++)
            if (_slots[i].Mapping is null && !_slots[i].Recorded) { _capture = i; break; }
        if (_capture < 0) FeedbackDropped++;
    }

    public void Configure(int view, float4x4 projection, uint width, uint height, float error, uint refinementLimit)
    {
        var lod = ProjectedGeometryError.PrepareLodProjection(projection, width, height);
        var value = new PbrGpuHierarchy.Configuration(projection,
            new(_hierarchy.RootBase, _hierarchy.RootCount, _hierarchy.PageCount, refinementLimit),
            new(width, height, error, 0),
            new(_hierarchy.SingleCapacity, _hierarchy.DoubleCapacity, view == 7 ? 0u : (uint)view,
                checked(_hierarchy.SingleCapacity + _hierarchy.DoubleCapacity)),
            lod.EyeNear, lod.ForwardPixels);
        Wgpu.WriteBuffer<PbrGpuHierarchy.Configuration>(_gpu.Queue, _configuration[view].GetWgpu<WGPUBuffer>(), 0, [value]);
    }

    public void Reset(WgpuHandle<WGPUCommandEncoder> encoder)
    {
        var pass = Wgpu.BeginComputePass(encoder, WGPUComputePassDescriptor.Default);
        try {
            Wgpu.SetBindGroup(pass, 0, _groups[7].GetWgpu<WGPUBindGroup>());
            Wgpu.SetComputePipeline(pass, _hierarchy.ResetFeedback.GetWgpu<WGPUComputePipeline>());
            Wgpu.DispatchWorkgroups(pass, ((_hierarchy.PageCount * 2) + 63) / 64);
        }
        finally { Wgpu.EndComputePass(pass); Wgpu.Release(ref pass); }
    }

    public void Select(WgpuHandle<WGPUCommandEncoder> encoder, int view)
    {
        SelectNodes(encoder, view);
        ExpandTriangles(encoder, view);
    }

    public void SelectNodes(WgpuHandle<WGPUCommandEncoder> encoder, int view)
    {
        var pass = Wgpu.BeginComputePass(encoder, WGPUComputePassDescriptor.Default);
        try {
            Wgpu.SetBindGroup(pass, 0, _groups[view].GetWgpu<WGPUBindGroup>());
            Wgpu.SetComputePipeline(pass, _hierarchy.ResetDraws.GetWgpu<WGPUComputePipeline>());
            Wgpu.DispatchWorkgroups(pass, 1);
            Wgpu.SetComputePipeline(pass, _hierarchy.SeedRoots.GetWgpu<WGPUComputePipeline>());
            Wgpu.DispatchWorkgroups(pass, (_hierarchy.RootCount + 63) / 64);
        }
        finally { Wgpu.EndComputePass(pass); Wgpu.Release(ref pass); }
        for (var level = 0; level < _hierarchy.Levels; level++) {
            var even = (level & 1) == 0;
            pass = Wgpu.BeginComputePass(encoder, WGPUComputePassDescriptor.Default);
            try {
                Wgpu.SetBindGroup(pass, 0, _groups[view].GetWgpu<WGPUBindGroup>());
                Wgpu.SetComputePipeline(pass, (even ? _hierarchy.PrepareEven : _hierarchy.PrepareOdd).GetWgpu<WGPUComputePipeline>());
                Wgpu.DispatchWorkgroups(pass, 1);
            }
            finally { Wgpu.EndComputePass(pass); Wgpu.Release(ref pass); }
            Wgpu.CopyBufferToBuffer(encoder, Arguments.GetWgpu<WGPUBuffer>(), 64, DispatchArguments.GetWgpu<WGPUBuffer>(), 0, 12);
            pass = Wgpu.BeginComputePass(encoder, WGPUComputePassDescriptor.Default);
            try {
                Wgpu.SetBindGroup(pass, 0, _groups[view].GetWgpu<WGPUBindGroup>());
                Wgpu.SetComputePipeline(pass, (even ? _hierarchy.TraverseEven : _hierarchy.TraverseOdd).GetWgpu<WGPUComputePipeline>());
                Wgpu.DispatchWorkgroupsIndirect(pass, DispatchArguments.GetWgpu<WGPUBuffer>(), 0);
            }
            finally { Wgpu.EndComputePass(pass); Wgpu.Release(ref pass); }
        }
        pass = Wgpu.BeginComputePass(encoder, WGPUComputePassDescriptor.Default);
        try {
            Wgpu.SetBindGroup(pass, 0, _groups[view].GetWgpu<WGPUBindGroup>());
            Wgpu.SetComputePipeline(pass, _hierarchy.PrepareExpansion.GetWgpu<WGPUComputePipeline>());
            Wgpu.DispatchWorkgroups(pass, 1);
        }
        finally { Wgpu.EndComputePass(pass); Wgpu.Release(ref pass); }
    }

    public void ExpandTriangles(WgpuHandle<WGPUCommandEncoder> encoder, int view)
    {
        // Indirect arguments cannot be writable storage in the consuming compute pass.
        var pass = Wgpu.BeginComputePass(encoder, WGPUComputePassDescriptor.Default);
        try {
            Wgpu.SetBindGroup(pass, 0, _expansionGroups[view].GetWgpu<WGPUBindGroup>());
            Wgpu.SetComputePipeline(pass, _hierarchy.Expand.GetWgpu<WGPUComputePipeline>());
            Wgpu.DispatchWorkgroupsIndirect(pass, Arguments.GetWgpu<WGPUBuffer>(), 64);
        }
        finally { Wgpu.EndComputePass(pass); Wgpu.Release(ref pass); }
    }

    public void CopyFeedback(WgpuHandle<WGPUCommandEncoder> encoder)
    {
        if (_capture < 0) return;
        var slot = _slots[_capture];
        Wgpu.CopyBufferToBuffer(encoder, Feedback.GetWgpu<WGPUBuffer>(), 0, slot.Buffer.GetWgpu<WGPUBuffer>(), 0, FeedbackBytes);
        Wgpu.CopyBufferToBuffer(encoder, Arguments.GetWgpu<WGPUBuffer>(), 0, slot.Buffer.GetWgpu<WGPUBuffer>(), FeedbackBytes, 64);
        slot.Frame = _clock;
        slot.Recorded = true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var slot in _slots) {
            if (slot.Mapping is not null) {
                Wgpu.UnmapBuffer(slot.Buffer.GetWgpu<WGPUBuffer>());
                _ = slot.Mapping.ContinueWith(static task => { _ = task.Exception; }, TaskScheduler.Default);
            }
        }
        _gpu.Dispose();
    }
}
