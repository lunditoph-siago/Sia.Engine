using System.Diagnostics;
using Sia;
using Sia.Engine.Camera;
using Sia.Engine.Mesh;
using Sia.Engine.Rendering.Pbr;
using Sia.Graphics.Reactive;
using Sia.Math;
using Sia.Reactive;
using Sia.RenderGraph;
using Sia.WebGPU;

namespace Sia.Engine.Rendering.Benchmarks;

internal sealed class BenchmarkScene : IDisposable
{
    private static readonly RenderGraphTextureKey s_Color = new("benchmark-color");
    private static readonly RenderGraphTextureKey s_Depth = new("benchmark-depth");
    private static readonly RenderGraphBufferKey s_Readback = new("benchmark-readback");
    private readonly GpuDevice _gpu;
    private readonly World _main = new();
    private readonly World _graphWorld = new();
    private readonly RenderWorld _render = new();
    private readonly VisibilityPbrFeature _feature;
    private readonly GpuFrame _frame;
    private readonly Entity _camera;
    private readonly Entity _readback;
    private readonly WgpuRenderGraphRegistry _registry;
    private readonly uint _width, _height;
    private readonly ulong _readbackSize;
    private RenderFeatureContext<RenderFrameContext> _context;
    private ReactiveMount<GraphProps>? _mount;

    public ulong BufferCapacityBytes => _registry.PreparePlan().Graph.Buffers.Aggregate(0ul, (sum, buffer) => sum + buffer.Descriptor.Size);
    public ulong WorkCapacityBytes => _feature.TriangleCapacity * 16ul;
    public int GraphPassCount => _registry.PreparePlan().Graph.Passes.Count;

    public BenchmarkScene(GpuDevice gpu, MeshPatchTree tree, VisibilityInstance[] instances, uint width, uint height, int triangleBudget)
    {
        _gpu = gpu; _width = width; _height = height;
        try {
            _frame = new(_main, _render.Entities,
                _render.Entities.OwnWgpu(gpu.Device, static (ref WgpuHandle<WGPUDevice> _) => { }),
                _render.Entities.OwnWgpu(gpu.Queue, static (ref WgpuHandle<WGPUQueue> _) => { }));
            _camera = _main.Create(HList.From(CameraMatrices.Identity));
            _main.AcquireAddon<Viewport>().Value = new((int)width, (int)height);
            _registry = _graphWorld.ConfigureWgpuRenderGraph(gpu.Device, gpu.Queue);
            _feature = VisibilityPbrFeature.CreateGpuLod(in _frame, tree, instances,
                new VisibilityAlbedo(1, 1, [new byte[] { 255, 255, 255, 255 }]),
                new(8, new(int.MaxValue, int.MaxValue, triangleBudget)), WGPUTextureFormat.RGBA8Unorm,
                enableGpuTiming: gpu.TimingEnabled);
            _readbackSize = 80ul + (gpu.TimingEnabled ? (ulong)VisibilityPbrFeature.GpuTimingStages.Length * 16 : 0);
            _readback = _render.Entities.CreateWgpuBuffer(_frame.Device,
                new WGPUBufferDescriptor { Size = _readbackSize, Usage = WGPUBufferUsage.CopyDst | WGPUBufferUsage.MapRead });
        }
        catch (Exception error) {
            try { Dispose(); }
            catch (Exception cleanupError) { error.Data["CleanupFailure"] = cleanupError; }
            throw;
        }
    }

    public FrameSample Render(float4x4 projection)
    {
        var clock = Stopwatch.StartNew();
        _camera.Get<CameraMatrices>() = CameraMatrices.Identity with { ViewProj = projection, WorldPosition = new float3(0, 0, 3) };
        _render.BeginFrame();
        _context = new(_render, _render.GetOrCreateView(new("benchmark")), new(_frame, _camera, s_Color, s_Depth));
        _feature.Prepare(in _context);
        var prepareMs = clock.Elapsed.TotalMilliseconds;
        clock.Restart();
        _mount ??= _graphWorld.Mount(BuildGraph, new GraphProps(this));
        _registry.Execute();
        var encodeMs = clock.Elapsed.TotalMilliseconds;
        clock.Restart();
        var buffer = _readback.GetWgpu<WGPUBuffer>();
        Wgpu.MapBufferRead(_gpu.Instance, buffer, 0, _readbackSize);
        uint[] status;
        ulong[] times;
        try {
            status = Wgpu.GetMappedRangeReadOnly<uint>(buffer, 0, 20).ToArray();
            times = _gpu.TimingEnabled ? Wgpu.GetMappedRangeReadOnly<ulong>(buffer, 80, VisibilityPbrFeature.GpuTimingStages.Length * 2).ToArray() : [];
        }
        finally { Wgpu.UnmapBuffer(buffer); }
        var waitMs = clock.Elapsed.TotalMilliseconds;
        _gpu.CheckErrors();
        if (status[0] % 3 != 0 || status[8] % 3 != 0 || status[1] != 1 || status[9] != 1 || status[10] != status[0]
            || ((ulong)status[0] + status[8]) / 3 > status[12] || status[12] > _feature.TriangleCapacity
            || status[18] > status[17] || status[17] > status[16] || status[19] > status[16]) {
            throw new InvalidOperationException("GPU statistics violate the visibility work-list contract.");
        }
        Dictionary<string, double>? durations = null;
        if (_gpu.TimingEnabled) {
            durations = [];
            for (var i = 0; i < times.Length; i += 2) {
                if (times[i + 1] < times[i]) { throw new InvalidOperationException("GPU timestamps are not ordered."); }
                durations.Add(VisibilityPbrFeature.GpuTimingStages[i / 2], (times[i + 1] - times[i]) / 1_000_000d);
            }
        }
        var error = BitConverter.UInt32BitsToSingle(status[7]);
        var triangles = (status[0] + status[8]) / 3;
        var counters = new FrameCounters(status[16], status[17], status[18], status[19],
            status[4], status[5], status[12], status[0] / 3, status[8] / 3,
            status[13], status[14], status[15], triangles * 16ul,
            triangles == 0 ? null : (double)_width * _height / triangles,
            float.IsFinite(error) ? error : null, !float.IsFinite(error), (status[6] & 1) != 0, (status[6] & 2) != 0);
        return new(prepareMs, encodeMs, waitMs, durations, counters);
    }

    private readonly record struct GraphProps(BenchmarkScene Scene);

    private static ReactiveNode BuildGraph(in GraphProps props, ref Hooks hooks)
    {
        var scene = props.Scene;
        var graph = new RenderGraphBuildContext(ref hooks, scene._registry);
        graph.UseTexture(s_Color, new("benchmark-color", RenderGraphTextureFormat.RGBA8Unorm, scene._width, scene._height));
        graph.UseTexture(s_Depth, new("benchmark-depth", RenderGraphTextureFormat.Depth32Float, scene._width, scene._height));
        scene._feature.BuildRenderGraph(ref graph, in scene._context);
        graph.ExportTexture(s_Color);
        graph.UseImportedBuffer(s_Readback, new("benchmark-readback", scene._readbackSize,
            RenderGraphBufferUsage.CopyDestination | RenderGraphBufferUsage.MapRead));
        graph.BindImportedBuffer(s_Readback, scene._readback.GetWgpu<WGPUBuffer>());
        graph.ExportBuffer(s_Readback, RenderGraphBufferUsage.MapRead);
        graph.UseComputePass(new("benchmark-readback"), "benchmark-readback", declaration => {
            declaration.Read(scene._feature.GpuStatisticsTarget, RenderGraphBufferUsage.CopySource)
                .Write(s_Readback, RenderGraphBufferUsage.CopyDestination);
            if (scene._gpu.TimingEnabled) { declaration.Read(scene._feature.GpuTimingsTarget, RenderGraphBufferUsage.CopySource); }
        }, context => {
            var destination = context.GetBuffer(s_Readback);
            Wgpu.CopyBufferToBuffer(context.CommandEncoder, context.GetBuffer(scene._feature.GpuStatisticsTarget), 0, destination, 0, 80);
            if (scene._gpu.TimingEnabled) {
                Wgpu.CopyBufferToBuffer(context.CommandEncoder, context.GetBuffer(scene._feature.GpuTimingsTarget), 0,
                    destination, 80, scene._readbackSize - 80);
            }
        });
        return Sia.Reactive.Reactive.None;
    }

    public void Dispose() => GpuDevice.DisposeAll(() => _mount?.Unmount(), _graphWorld.Dispose, _render.Dispose, _main.Dispose);
}

internal sealed record FrameSample(double PrepareMilliseconds, double EncodeAndSubmitMilliseconds, double WaitAndReadMilliseconds,
    IReadOnlyDictionary<string, double>? GpuMilliseconds, FrameCounters Counters);

internal sealed record FrameCounters(uint ProjectedNodeInstances, uint RefinementCandidates, uint Refinements, uint PeakHeapEntries,
    uint SelectedPatches, uint SelectedMeshlets, uint SelectedTriangles, uint MainTriangles, uint PostTriangles,
    uint FrustumRejectedPatches, uint HistoryDeferredPatches, uint RecoveredPatches, ulong WorkBytesWritten,
    double? ViewportPixelsPerEmittedTriangle, double? MaximumEstimatedPixelError, bool UnboundedEstimatedPixelError,
    bool BudgetLimited, bool BudgetUnreachable);
