using System.Diagnostics;
using Sia;
using Sia.Graphics.Reactive;
using Sia.WebGPU;

namespace Sia.Engine.Example;

internal sealed unsafe partial class SceneExampleApp
{
    private static readonly RenderGraphBufferKey _offscreenReadbackKey = new("offscreen-completion");
    private readonly Entity[] _offscreenBuffers = new Entity[3];
    private readonly Task?[] _offscreenMappings = new Task?[3];
    private int _offscreenSlot = 0;

#if !BROWSER
    private void RetireOffscreen(int index)
    {
        if (_offscreenMappings[index] is not { } mapping) return;
        try {
            while (!mapping.IsCompleted) {
                ThrowGpuError();
                Wgpu.ProcessEvents(_instance);
                Thread.Yield();
            }
            mapping.GetAwaiter().GetResult();
            var pixel = Wgpu.GetMappedRangeReadOnly<byte>(_offscreenBuffers[index].GetWgpu<WGPUBuffer>(), 0, 4);
            if (pixel[3] != 255) throw new InvalidOperationException("Offscreen output did not produce an opaque pixel.");
        }
        finally {
            Wgpu.UnmapBuffer(_offscreenBuffers[index].GetWgpu<WGPUBuffer>());
            _offscreenMappings[index] = null;
        }
    }

    private void RenderOffscreenFrame()
    {
        var start = Stopwatch.GetTimestamp();
        RetireOffscreen(_offscreenSlot);
        _acquireMilliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        if (!_offscreenBuffers[_offscreenSlot].IsValid) {
            _offscreenBuffers[_offscreenSlot] = _renderWorld!.Entities.CreateWgpuBuffer(_renderDevice,
                new WGPUBufferDescriptor { Size = 256, Usage = WGPUBufferUsage.MapRead | WGPUBufferUsage.CopyDst });
        }
        UpdateRenderGraph(default);
        var encode = Stopwatch.GetTimestamp();
        ExecuteRenderGraph();
        SubmitGpuTiming();
        _encodeMilliseconds = Stopwatch.GetElapsedTime(encode).TotalMilliseconds;
        _offscreenMappings[_offscreenSlot] = Wgpu.MapBufferReadAsync(_offscreenBuffers[_offscreenSlot].GetWgpu<WGPUBuffer>(), 0, 256);
        _offscreenSlot = (_offscreenSlot + 1) % _offscreenBuffers.Length;
        _presentMilliseconds = 0;
        RecordBenchmark(start);
    }

    private void DrainOffscreen()
    {
        List<Exception>? errors = null;
        for (var i = 0; i < _offscreenMappings.Length; i++) {
            try { RetireOffscreen(i); }
            catch (Exception error) { (errors ??= []).Add(error); }
        }
        if (errors is not null) throw new AggregateException(errors);
    }
#endif
}
