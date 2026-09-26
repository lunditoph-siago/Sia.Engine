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

internal sealed class VisibilityCacheVerification : IDisposable
{
    private static readonly RenderGraphTextureKey s_Color = new("cache-check-color"), s_Depth = new("cache-check-depth");
    private static readonly RenderGraphBufferKey s_Readback = new("cache-check-pixels");
    private readonly GpuDevice _gpu;
    private readonly bool _timingEnabled;
    private readonly World _main = new(), _graphWorld = new();
    private readonly RenderWorld _render = new();
    private readonly GpuFrame _frame;
    private readonly Entity _camera, _pixels;
    private readonly VisibilityPbrFeature _feature;
    private readonly WgpuRenderGraphRegistry _registry;
    private RenderFeatureContext<RenderFrameContext> _context;
    private readonly record struct GraphProps(VisibilityCacheVerification Scene);
    private ReactiveMount<GraphProps>? _mount;

    private VisibilityCacheVerification(GpuDevice gpu, PbrSceneAsset asset, bool timing = false)
    {
        _gpu = gpu;
        _timingEnabled = timing && gpu.TimingEnabled;
        try {
            _frame = new(_main, _render.Entities,
                _render.Entities.OwnWgpu(gpu.Device, static (ref WgpuHandle<WGPUDevice> _) => { }),
                _render.Entities.OwnWgpu(gpu.Queue, static (ref WgpuHandle<WGPUQueue> _) => { }));
            _camera = _main.Create(HList.From(CameraMatrices.Identity));
            _main.AcquireAddon<Viewport>().Value = new(128, 128);
            _registry = _graphWorld.ConfigureWgpuRenderGraph(gpu.Device, gpu.Queue);
            _feature = VisibilityPbrFeature.CreateFixedScene(in _frame, asset,
                asset.Instances.ToArray().Select(i => new VisibilityInstance(i.Transform, i.Material) { AssetIndex = i.Geometry }).ToArray(),
                WGPUTextureFormat.RGBA8Unorm, enableGpuTiming: _timingEnabled);
            _pixels = _render.Entities.CreateWgpuBuffer(_frame.Device, new WGPUBufferDescriptor {
                Size = 128 * 128 * 4 + (ulong)VisibilityPbrFeature.GpuTimingStages.Length * 16, Usage = WGPUBufferUsage.MapRead | WGPUBufferUsage.CopyDst });
        }
        catch { Dispose(); throw; }
    }

    private async Task<byte[]> FrameAsync(float4x4 projection)
    {
        _camera.Get<CameraMatrices>() = CameraMatrices.Identity with { ViewProj = projection, WorldPosition = new(0, 0, 3) };
        _render.BeginFrame();
        _context = new(_render, _render.GetOrCreateView(new("cache-check")), new(_frame, _camera, s_Color, s_Depth));
        _feature.Extract(in _context); _feature.Prepare(in _context);
        if (_mount is { } mount) mount.Update(new(this));
        else _mount = _graphWorld.Mount(BuildGraph, new GraphProps(this));
        _graphWorld.FlushReactive(); _registry.Execute();
        var buffer = _pixels.GetWgpu<WGPUBuffer>();
        var mapping = Wgpu.MapBufferReadAsync(buffer, 0, Wgpu.GetBufferSize(buffer));
        while (!mapping.IsCompleted) { Wgpu.ProcessEvents(_gpu.Instance); if (!mapping.IsCompleted) await Task.WhenAny(mapping, Task.Delay(1)); }
        await mapping;
        try {
            _gpu.CheckErrors();
            if (_timingEnabled) {
                var times = Wgpu.GetMappedRangeReadOnly<ulong>(buffer, 128 * 128 * 4, VisibilityPbrFeature.GpuTimingStages.Length * 2);
                for (var i = 0; i < times.Length; i += 2)
                    if (times[i + 1] < times[i]) throw new InvalidOperationException("Invalid timing pair.");
                if (times[0] != 0 || times[1] != 0)
                    throw new InvalidOperationException("Fixed rendering resolved unused LOD queries.");
                if (_feature.FrameStatistics.VisibilityCacheHits != 0 && (times[2] != 0 || times[3] != 0))
                    throw new InvalidOperationException("Cached rendering retained stale HZB queries.");
                if (times[4] == 0 || times[5] == 0)
                    throw new InvalidOperationException("Fixed rendering did not write raster timestamps.");
            }
            return Wgpu.GetMappedRangeReadOnly<byte>(buffer, 0, 128 * 128 * 4).ToArray();
        }
        finally { Wgpu.UnmapBuffer(buffer); }
    }

    private static ReactiveNode BuildGraph(in GraphProps props, ref Hooks hooks)
    {
        var owner = props.Scene;
        var graph = new RenderGraphBuildContext(ref hooks, owner._registry);
        graph.UseTexture(s_Color, new("cache-check-color", RenderGraphTextureFormat.RGBA8Unorm, 128, 128));
        graph.UseTexture(s_Depth, new("cache-check-depth", RenderGraphTextureFormat.Depth32Float, 128, 128));
        owner._feature.BuildRenderGraph(ref graph, in owner._context);
        graph.UseImportedBuffer(s_Readback, new("cache-check-pixels", Wgpu.GetBufferSize(owner._pixels.GetWgpu<WGPUBuffer>()),
            RenderGraphBufferUsage.CopyDestination | RenderGraphBufferUsage.MapRead));
        graph.BindImportedBuffer(s_Readback, owner._pixels.GetWgpu<WGPUBuffer>());
        graph.ExportBuffer(s_Readback, RenderGraphBufferUsage.MapRead);
        graph.UseComputePass(new("cache-check-copy"), "cache-check-copy", declaration => {
            declaration.Read(s_Color, RenderGraphTextureUsage.CopySource).Write(s_Readback, RenderGraphBufferUsage.CopyDestination);
            if (owner._timingEnabled) declaration.Read(owner._feature.GpuTimingsTarget, RenderGraphBufferUsage.CopySource);
        }, context => {
            Wgpu.CopyTextureToBuffer(context.CommandEncoder, context.GetTexture(s_Color), context.GetBuffer(s_Readback), 128, 128, 512);
            if (owner._timingEnabled) Wgpu.CopyBufferToBuffer(context.CommandEncoder, context.GetBuffer(owner._feature.GpuTimingsTarget), 0,
                context.GetBuffer(s_Readback), 128 * 128 * 4, (ulong)VisibilityPbrFeature.GpuTimingStages.Length * 16);
        });
        return Sia.Reactive.Reactive.None;
    }

    public static async Task RunAsync()
    {
        using var gpu = await GpuDevice.CreateAsync(true);
        var mesh = MeshPatchAsset.Cook(Assets.Grid(16));
        var transformed = PbrSceneAsset.Create([mesh], [new(PbrMaterial.Default, DoubleSided: true)],
            [new(0, 0, float4x4.Scale(new float3(.6f, .6f, 1))), new(0, 0, float4x4.Translate(new(.3f, 0, .2f)))]);
        var worldSpace = PbrSceneAsset.Create([mesh], [new(PbrMaterial.Default, DoubleSided: true)], [new(0, 0, float4x4.identity)]);
        Console.WriteLine($"Fixed timing adapter: {gpu.Description}; timestamps={gpu.TimingEnabled}.");
        if (!gpu.TimingEnabled) throw new NotSupportedException("The timing regression requires timestamp-query support.");
        foreach (var asset in new[] { transformed, worldSpace }) {
            using var timed = new VisibilityCacheVerification(gpu, asset, timing: true);
            using var untimed = new VisibilityCacheVerification(gpu, asset);
            foreach (var shear in new[] { 0f, .8f, .8f, -.8f, 0f }) {
                var projection = float4x4.identity; projection.c2.x = shear;
                var expected = await untimed.FrameAsync(projection);
                var actual = await timed.FrameAsync(projection);
                if (!expected.AsSpan().SequenceEqual(actual)) throw new InvalidOperationException("Fixed GPU timing changed raster output.");
            }
        }
        foreach (var asset in new[] { transformed, worldSpace }) {
            using var cached = new VisibilityCacheVerification(gpu, asset);
            var projection = float4x4.identity;
            var first = await cached.FrameAsync(projection);
            if (first.Where((_, i) => i % 4 != 3).All(value => value == 0)) throw new InvalidOperationException("Pixel fixture is empty.");
            var reused = await cached.FrameAsync(projection);
            if (cached._feature.FrameStatistics.VisibilityCacheHits != 1 || !first.AsSpan().SequenceEqual(reused))
                throw new InvalidOperationException("Static cache hit differs from its fully rendered frame.");
            foreach (var shear in new[] { .8f, -.8f, 0f }) {
                projection.c2.x = shear;
                using var fresh = new VisibilityCacheVerification(gpu, asset);
                var expected = await fresh.FrameAsync(projection);
                if (shear != 0 && first.AsSpan().SequenceEqual(expected))
                    throw new InvalidOperationException("The camera change did not exercise different pixels.");
                var actual = await cached.FrameAsync(projection);
                if (cached._feature.FrameStatistics.VisibilityCacheHits != 0 || !expected.AsSpan().SequenceEqual(actual))
                    throw new InvalidOperationException("Camera change/history recovery differs from a fresh frame.");
                reused = await cached.FrameAsync(projection);
                if (cached._feature.FrameStatistics.VisibilityCacheHits != 1 || !expected.AsSpan().SequenceEqual(reused))
                    throw new InvalidOperationException("Combined main/post index cache differs from a fresh frame.");
            }
        }
        Console.WriteLine("Visibility cache verification passed: transformed and world-space RGBA pixels equal fresh rendering on static hits, camera changes, history recovery, and subsequent combined-list hits.");
    }

    public void Dispose() => GpuDevice.DisposeAll(() => _mount?.Unmount(), _graphWorld.Dispose, _render.Dispose, _main.Dispose);
}
