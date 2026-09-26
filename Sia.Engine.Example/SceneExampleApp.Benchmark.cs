using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sia.Asset;
using Sia.Engine.Rendering.Pbr;
using Sia.GLFW;

namespace Sia.Engine.Example;

internal sealed partial class SceneExampleApp
{
    private sealed record Timing(double Mean, double Median, double P95, double P99, double Max);
    private sealed record CpuStages(double Acquire, double Extract, double PrepareAndQueue, double GraphUpdate, double EncodeAndSubmit, double Present);
    private sealed record FrameSample(double CpuMilliseconds, double CadenceMilliseconds, CpuStages CpuStages, VisibilityFrameStatistics? Visibility, float RenderScale);
    private sealed record BenchmarkReport(string Mode, string Quality, int Width, int Height, int RenderWidth, int RenderHeight,
        string Adapter, string PresentMode, string[] Passes, double FirstSubmittedFrameMilliseconds,
        int WarmupFrames, int SampleFrames, bool Moving, Timing CpuFrameMilliseconds, Timing FrameCadenceMilliseconds,
        PbrStreamingStatistics? Streaming, AssetChunkCacheStatistics? Chunks, long ManagedHeapBytes, FrameSample[] Frames,
        bool GpuTimingEnabled, int GpuTimingDropped, int GpuTimingPending, GpuSample[] GpuFrames,
        int TargetFps, int ResolutionChanges);
    [JsonSerializable(typeof(BenchmarkReport))]
    private partial class BenchmarkJsonContext : JsonSerializerContext;
    private readonly List<double> _submissionSamples = [], _cadenceSamples = [];
    private readonly List<FrameSample> _frameSamples = [];
    private long _previousBenchmarkFrame;
    private double _firstFrameMilliseconds;
    private int _benchmarkFrames;
    private double _acquireMilliseconds, _extractMilliseconds, _prepareMilliseconds, _graphMilliseconds, _encodeMilliseconds, _presentMilliseconds;
    private void RecordBenchmark(long start)
    {
        if (_benchmarkFrames++ == 0) {
            _firstFrameMilliseconds = Program.StartupClock.Elapsed.TotalMilliseconds;
            Console.WriteLine($"PBR first submitted frame: {_firstFrameMilliseconds:F2} ms from managed entry.");
        }
        if (Program.BenchmarkFrames == 0) return;
        if (_benchmarkFrames > 120) {
            _submissionSamples.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            _cadenceSamples.Add(Stopwatch.GetElapsedTime(_previousBenchmarkFrame, start).TotalMilliseconds);
            _frameSamples.Add(new(_submissionSamples[^1], _cadenceSamples[^1],
                new(_acquireMilliseconds, _extractMilliseconds, _prepareMilliseconds, _graphMilliseconds, _encodeMilliseconds, _presentMilliseconds),
                _visibilityLod?.FrameStatistics, _renderScale));
        }
        _previousBenchmarkFrame = start;
        if (_benchmarkFrames != Program.BenchmarkFrames + 120) return;
        Console.WriteLine("BISTRO_BENCHMARK " + JsonSerializer.Serialize(new BenchmarkReport(
            _materialStream is null ? (_finest ? "finest" : "auto") : "streaming", Program.Quality.ToString(),
            OutputWidth, OutputHeight, RenderWidth, RenderHeight, _adapterDescription,
            Program.Offscreen ? "OffscreenReadback3" : _presentMode.ToString(),
            _renderGraph!.PreparePlan().Graph.Passes.Select(pass => pass.Name).ToArray(),
            _firstFrameMilliseconds, 120, Program.BenchmarkFrames, Program.BenchmarkMotion,
            Summarize(_submissionSamples), Summarize(_cadenceSamples), _visibilityLod?.StreamingStatistics,
            _materialStream?.Statistics, GC.GetTotalMemory(false), _frameSamples.ToArray(), _gpuTimingEnabled,
            _timingDropped, _timingSlots.Count(s => s.Mapping is not null), _gpuSamples.ToArray(),
            Program.TargetFps, _resolutionController?.Changes ?? 0), BenchmarkJsonContext.Default.BenchmarkReport));
        Glfw.RequestClose(_window);
    }
    private static Timing Summarize(List<double> samples)
    {
        var sorted = samples.Order().ToArray();
        return new(samples.Average(), sorted[sorted.Length / 2], sorted[(int)System.Math.Ceiling(sorted.Length * .95) - 1],
            sorted[(int)System.Math.Ceiling(sorted.Length * .99) - 1], sorted[^1]);
    }
}
