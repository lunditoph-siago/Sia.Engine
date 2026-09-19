using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sia.Asset;
using Sia.Engine.Rendering.Pbr;
using Sia.GLFW;

namespace Sia.Engine.Example;

internal sealed partial class SceneExampleApp
{
    private sealed record Timing(double Median, double P95, double P99, double Max);
    private sealed record FrameSample(double CpuMilliseconds, double CadenceMilliseconds, VisibilityFrameStatistics? Visibility);
    private sealed record BenchmarkReport(string Mode, int Width, int Height, double FirstSubmittedFrameMilliseconds,
        int WarmupFrames, int SampleFrames, bool Moving, Timing CpuFrameMilliseconds, Timing FrameCadenceMilliseconds,
        PbrStreamingStatistics? Streaming, AssetChunkCacheStatistics? Chunks, long ManagedHeapBytes, FrameSample[] Frames);
    [JsonSerializable(typeof(BenchmarkReport))]
    private partial class BenchmarkJsonContext : JsonSerializerContext;
    private readonly List<double> _submissionSamples = [], _cadenceSamples = [];
    private readonly List<FrameSample> _frameSamples = [];
    private long _previousBenchmarkFrame;
    private double _firstFrameMilliseconds;
    private int _benchmarkFrames;
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
            _frameSamples.Add(new(_submissionSamples[^1], _cadenceSamples[^1], _visibilityLod?.FrameStatistics));
        }
        _previousBenchmarkFrame = start;
        if (_benchmarkFrames != Program.BenchmarkFrames + 120) return;
        Console.WriteLine("BISTRO_BENCHMARK " + JsonSerializer.Serialize(new BenchmarkReport(
            _materialStream is null ? (_finest ? "finest" : "auto") : "streaming", _framebufferWidth, _framebufferHeight,
            _firstFrameMilliseconds, 120, Program.BenchmarkFrames, Program.BenchmarkMotion,
            Summarize(_submissionSamples), Summarize(_cadenceSamples), _visibilityLod?.StreamingStatistics,
            _materialStream?.Statistics, GC.GetTotalMemory(false), _frameSamples.ToArray()), BenchmarkJsonContext.Default.BenchmarkReport));
        Glfw.RequestClose(_window);
    }
    private static Timing Summarize(List<double> samples)
    {
        var sorted = samples.Order().ToArray();
        return new(sorted[sorted.Length / 2], sorted[(int)System.Math.Ceiling(sorted.Length * .95) - 1],
            sorted[(int)System.Math.Ceiling(sorted.Length * .99) - 1], sorted[^1]);
    }
}
