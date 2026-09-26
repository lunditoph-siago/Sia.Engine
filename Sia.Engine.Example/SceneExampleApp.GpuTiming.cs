using Sia;
using Sia.Engine.Rendering.Pbr;
using Sia.Graphics.Reactive;
using Sia.WebGPU;

namespace Sia.Engine.Example;

internal sealed unsafe partial class SceneExampleApp
{
    private static readonly RenderGraphBufferKey _gpuReadbackKey = new("example-gpu-timing");
    private bool _gpuTimingEnabled;
    private readonly TimingSlot[] _timingSlots = Enumerable.Range(0, 4).Select(_ => new TimingSlot()).ToArray();
    private Entity _timingSink;
    private int _timingSlot = -1, _timingDropped;
    private readonly List<GpuSample> _gpuSamples = [];
    private static ulong TimingBytes => (ulong)VisibilityPbrFeature.GpuTimingStages.Length * 16;
    private sealed class TimingSlot {
        public Entity Buffer;
        public Task? Mapping;
        public int Sequence, Width, Height;
        public float Scale;
    }
    private sealed record GpuSample(int Sequence, int Width, int Height, double SpanMilliseconds, Dictionary<string, double> Stages);

    private Entity PrepareGpuTiming()
    {
        if (!_gpuTimingEnabled || _visibilityLod is null || _materialStream is not null) return default;
        _timingSlot = -1;
        for (var i = 0; i < _timingSlots.Length; i++) {
            var slot = _timingSlots[i];
            if (slot.Mapping is { IsCompleted: true } mapping) {
                try {
                    mapping.GetAwaiter().GetResult();
                    var times = Wgpu.GetMappedRangeReadOnly<ulong>(slot.Buffer.GetWgpu<WGPUBuffer>(), 0, (int)TimingBytes / 8);
                    var stages = new Dictionary<string, double>();
                    ulong first = ulong.MaxValue, last = 0;
                    for (var t = 0; t < times.Length; t += 2) {
                        if (times[t] == 0 && times[t + 1] == 0) continue;
                        if (times[t + 1] < times[t]) throw new InvalidOperationException("GPU timestamps out of order.");
                        stages.Add(VisibilityPbrFeature.GpuTimingStages[t / 2], (times[t + 1] - times[t]) / 1_000_000d);
                        first = System.Math.Min(first, times[t]); last = System.Math.Max(last, times[t + 1]);
                    }
                    if (stages.Count != 0) {
                        var span = (last - first) / 1_000_000d;
                        _resolutionController?.Observe(slot.Sequence, _benchmarkFrames, slot.Scale, span);
                        if (Program.BenchmarkFrames > 0 && slot.Sequence >= 120)
                            _gpuSamples.Add(new(slot.Sequence, slot.Width, slot.Height, span, stages));
                    }
                } finally { Wgpu.UnmapBuffer(slot.Buffer.GetWgpu<WGPUBuffer>()); slot.Mapping = null; }
            }
            if (slot.Mapping is null && _timingSlot < 0) _timingSlot = i;
        }
        _visibilityLod.SampleGpuTiming = _benchmarkFrames % 16 == 0 && _timingSlot >= 0;
        if (!_visibilityLod.SampleGpuTiming) {
            if (_benchmarkFrames % 16 == 0) _timingDropped++;
            _timingSlot = -1;
            if (!_timingSink.IsValid) _timingSink = CreateTimingBuffer();
            return _timingSink;
        }
        var selected = _timingSlots[_timingSlot];
        if (!selected.Buffer.IsValid) selected.Buffer = CreateTimingBuffer();
        selected.Sequence = _benchmarkFrames; selected.Width = RenderWidth; selected.Height = RenderHeight;
        selected.Scale = _renderScale;
        return selected.Buffer;
    }

    private Entity CreateTimingBuffer() => _renderWorld!.Entities.CreateWgpuBuffer(_renderDevice,
        new WGPUBufferDescriptor { Size = TimingBytes, Usage = WGPUBufferUsage.CopyDst | WGPUBufferUsage.MapRead });

    private void SubmitGpuTiming()
    {
        if (_timingSlot < 0) return;
        var slot = _timingSlots[_timingSlot];
        slot.Mapping = Wgpu.MapBufferReadAsync(slot.Buffer.GetWgpu<WGPUBuffer>(), 0, TimingBytes);
        _timingSlot = -1;
    }

    private bool DrainGpuTimingStep()
    {
        Wgpu.ProcessEvents(_instance);
        PrepareGpuTiming();
        _timingSlot = -1;
        return _timingSlots.Any(s => s.Mapping is not null);
    }
}
