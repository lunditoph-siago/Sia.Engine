using System.Text.Json;
using System.Text.Json.Serialization;
using Sia.Asset;
using Sia.Engine.Rendering.Pbr;
using Sia.GLFW;

namespace Sia.Engine.Example;

internal sealed partial class SceneExampleApp
{
    private readonly record struct FrameGc(int Gen0, int Gen1, int Gen2, double PauseMilliseconds);

    private sealed record FrameSample(PbrFrameStatistics? Visibility, float RenderScale, PbrStreamingStatistics? Streaming,
        PbrTextureStreamingStatistics? Textures, PbrGpuTraversalStatistics? GpuTraversal,
        long RenderThreadAllocatedBytes, FrameGc Gc);

    private sealed record ManagedMemory(long FirstFrameAllocatedBytes, long FirstFrameHeapBytes,
        long MeasuredAllocatedBytes, int Gen0Collections, int Gen1Collections, int Gen2Collections,
        double PauseMilliseconds, long CommittedBytes);

    private sealed record Workflow(string OpaquePath, bool BakedEnvironment, bool Finest, float PixelError,
        float ShadowTexelError, uint ShadowResolution, bool SurfaceData, bool DynamicProbes,
        bool BakedProbes, uint ProbeUpdates, uint ProbeSamples);

    private Workflow? _workflow;

    private sealed record BenchmarkReport(string Mode, string Quality, int Width, int Height, int RenderWidth, int RenderHeight,
        string Adapter, string PresentMode, string[] Passes,
        int WarmupFrames, int SampleFrames, bool Moving,
        AssetChunkCacheStatistics? Chunks, long ManagedHeapBytes, FrameSample[] Frames,
        bool GpuTimingEnabled, int GpuTimingDropped, int GpuTimingPending, GpuSample[] GpuFrames,
        int TargetFps, int ResolutionChanges, int GraphCompilationCount, Workflow? Workflow, ManagedMemory Memory);

    [JsonSerializable(typeof(BenchmarkReport))]
    private partial class BenchmarkJsonContext : JsonSerializerContext;

    private readonly List<FrameSample> _frameSamples = [];
    private long _firstFrameAllocatedBytes, _firstFrameHeapBytes, _measuredAllocatedStart;
    private int _gen0Start, _gen1Start, _gen2Start;
    private TimeSpan _pauseStart;
    private int _benchmarkFrames;

    private void RecordBenchmark(long allocatedStart)
    {
        if (_benchmarkFrames++ == 0) {
            _firstFrameAllocatedBytes = GC.GetTotalAllocatedBytes(false);
            _firstFrameHeapBytes = GC.GetTotalMemory(false);
        }
        if (Program.BenchmarkFrames == 0)
            return;
        if (_benchmarkFrames == 120) {
            _measuredAllocatedStart = GC.GetTotalAllocatedBytes(false);
            _gen0Start = GC.CollectionCount(0);
            _gen1Start = GC.CollectionCount(1);
            _gen2Start = GC.CollectionCount(2);
            _pauseStart = GC.GetTotalPauseDuration();
        }
        if (_benchmarkFrames > 120) {
            _frameSamples.Add(new(_sceneRenderer?.FrameStatistics, _renderScale, _sceneRenderer?.StreamingStatistics, _sceneRenderer?.TextureStreamingStatistics,
                _sceneRenderer?.GpuTraversalStatistics, GC.GetAllocatedBytesForCurrentThread() - allocatedStart,
                new(GC.CollectionCount(0) - _gen0Start, GC.CollectionCount(1) - _gen1Start, GC.CollectionCount(2) - _gen2Start,
                    (GC.GetTotalPauseDuration() - _pauseStart).TotalMilliseconds)));
        }
        if (_benchmarkFrames != Program.BenchmarkFrames + 120)
            return;
        Console.WriteLine("BISTRO_BENCHMARK " + JsonSerializer.Serialize(new BenchmarkReport(
            _pipeline == ScenePipeline.Unlit ? "unlit" : _materialStream is not null ? "hierarchical-stream" : _finest ? "resident-finest" : "resident-two-level", Program.Quality.ToString(),
            _framebufferWidth, _framebufferHeight, RenderWidth, RenderHeight, _adapterDescription,
            _presentMode.ToString(),
            _renderGraph!.PreparePlan().Graph.Passes.Select(pass => pass.Name).ToArray(),
            120, Program.BenchmarkFrames, Program.BenchmarkMotion,
            _materialStream?.Statistics, GC.GetTotalMemory(false), _frameSamples.ToArray(), _gpuTimingEnabled,
            _timingDropped, _timingSlots.Count(s => s.Mapping is not null), _gpuSamples.ToArray(),
            Program.TargetFps, _resolutionController?.Changes ?? 0, _renderGraph.CompilationCount, _workflow,
            new(_firstFrameAllocatedBytes, _firstFrameHeapBytes, GC.GetTotalAllocatedBytes(false) - _measuredAllocatedStart,
                GC.CollectionCount(0) - _gen0Start, GC.CollectionCount(1) - _gen1Start, GC.CollectionCount(2) - _gen2Start,
                (GC.GetTotalPauseDuration() - _pauseStart).TotalMilliseconds, GC.GetGCMemoryInfo().TotalCommittedBytes)), BenchmarkJsonContext.Default.BenchmarkReport));
        Glfw.RequestClose(_window);
    }
}
