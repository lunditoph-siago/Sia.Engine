using Sia;
using Sia.Engine.Camera;
using Sia.Engine.Lighting;
using Sia.Engine.Mesh;
using Sia.Engine.Rendering.Pbr;
using Sia.Graphics.Reactive;
using Sia.Math;
using Sia.Reactive;
using Sia.RenderGraph;
using Sia.WebGPU;

namespace Sia.Engine.Rendering.Benchmarks;

internal sealed partial class VisibilityCacheVerification : IDisposable
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
    private uint _renderSize;
    private PbrRenderFeature? _pbr;
    private Entity _sun, _caster;
    private readonly WgpuRenderGraphRegistry _registry;
    private RenderFeatureContext<RenderFrameContext> _context;
    private readonly record struct GraphProps(VisibilityCacheVerification Scene);
    private ReactiveMount<GraphProps>? _mount;

    private VisibilityCacheVerification(GpuDevice gpu, PbrSceneAsset asset, uint renderSize = 128, bool lod = false,
        bool timing = true, bool pbr = false, bool shadowsEnabled = true)
    {
        _gpu = gpu;
        _timingEnabled = timing && gpu.TimingEnabled;
        _renderSize = renderSize;
        try {
            _frame = new(_main, _render.Entities,
                _render.Entities.OwnWgpu(gpu.Device, static (ref WgpuHandle<WGPUDevice> _) => { }),
                _render.Entities.OwnWgpu(gpu.Queue, static (ref WgpuHandle<WGPUQueue> _) => { }));
            _camera = _main.Create(HList.From(CameraMatrices.Identity, global::Sia.Engine.Camera.Camera.Default,
                new GlobalTransform(new AffineTransform(new float3(0,0,3), quaternion.identity))));
            _main.AcquireAddon<Viewport>().Value = new((int)renderSize, (int)renderSize);
            _registry = _graphWorld.ConfigureWgpuRenderGraph(gpu.Device, gpu.Queue);
            _feature = lod ? VisibilityPbrFeature.CreateGpuScene(in _frame, asset, 32,
                new VisibilityLodSettings(0, new(10000, 10000, 100000)), WGPUTextureFormat.RGBA8Unorm, enableGpuTiming: _timingEnabled)
                : VisibilityPbrFeature.CreateFixedScene(in _frame, asset,
                asset.Instances.ToArray().Select(i => new VisibilityInstance(i.Transform, i.Material) { AssetIndex = i.Geometry }).ToArray(),
                WGPUTextureFormat.RGBA8Unorm, enableGpuTiming: _timingEnabled);
            if (lod) {
                foreach (var i in asset.Instances.Span) _caster = _main.Create(HList.From(new VisibilityInstance(i.Transform, i.Material) { AssetIndex = i.Geometry }));
            }
            if (pbr) {
                var shadows = _main.AcquireAddon<ShadowAtlasConfig>();
                shadows.CascadeCount = 1; shadows.TileResolution = 64; shadows.MaxShadowedSpotLights = 0;
                _sun = shadowsEnabled
                    ? _main.Create(HList.From(new DirectionalLight(), new ShadowCaster(), new LightColor(new(1,1,1), 2), new GlobalTransform(AffineTransform.Identity)))
                    : _main.Create(HList.From(new DirectionalLight(), new LightColor(new(1,1,1), 2), new GlobalTransform(AffineTransform.Identity)));
                _main.Create(HList.From(new PointLight(8), new LightColor(new(.6f,.8f,1), 12),
                    new GlobalTransform(new AffineTransform(new float3(0,0,2), quaternion.identity))));
                var renderer = new PbrRenderer(ClusterLightCullingPipeline.Create(_render.Entities, _frame.Device),
                    PbrIblPrecomputePipelines.Create(_render.Entities, _frame.Device),
                    PbrOutputPipelines.Create(_render.Entities, _frame.Device, WGPUTextureFormat.RGBA8Unorm));
                _pbr = new(renderer, _feature, new() { ScreenSpaceReflections = false, ScreenSpaceIndirectLighting = false });
            }
            gpu.CheckErrors();
            _pixels = _render.Entities.CreateWgpuBuffer(_frame.Device, new WGPUBufferDescriptor {
                Size = 128 * 128 * 4 + (ulong)VisibilityPbrFeature.GpuTimingStages.Length * 16, Usage = WGPUBufferUsage.MapRead | WGPUBufferUsage.CopyDst });
        }
        catch { Dispose(); throw; }
    }

    private async Task<byte[]> FrameAsync(float4x4 projection, uint? renderSize = null)
    {
        if (renderSize is { } size) {
            _renderSize = size;
            _main.AcquireAddon<Viewport>().Value = new((int)size, (int)size);
        }
        _camera.Get<CameraMatrices>() = CameraMatrices.Identity with { ViewProj = projection, WorldPosition = new(0, 0, 3) };
        if (_pbr is not null) {
            var view = float4x4.Translate(new(0, 0, -3));
            var proj = math.mul(projection, float4x4.PerspectiveFov(MathF.PI / 3, 1, .1f, 1000));
            var vp = math.mul(proj, view);
            _camera.Get<CameraMatrices>() = new(view, proj, vp, math.inverse(vp), new(0,0,3), default);
        }
        _render.BeginFrame();
        _context = new(_render, _render.GetOrCreateView(new("cache-check")), new(_frame, _camera, s_Color, s_Depth));
        if (_pbr is not null) { _pbr.Extract(in _context); _pbr.Prepare(in _context); }
        else { _feature.Extract(in _context); _feature.Prepare(in _context); }
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
                if (_pbr is not null && _feature.SampleGpuTiming) {
                    var begin = VisibilityPbrFeature.GpuTimingStages.IndexOf("pbr-frame-begin") * 2;
                    var end = VisibilityPbrFeature.GpuTimingStages.IndexOf("pbr-frame-end") * 2;
                    if (times[begin] == 0 || times[end + 1] <= times[begin])
                        throw new InvalidOperationException("Missing full-frame PBR timestamps.");
                }
                for (var i = 0; i < times.Length; i += 2)
                    if (times[i + 1] < times[i]) throw new InvalidOperationException("Invalid timing pair.");
                if (_feature.FrameStatistics.VisibilityCacheHits != 0 && (times[2] != 0 || times[3] != 0))
                    throw new InvalidOperationException("Cached frame reused stale HZB timestamps.");
                if (_feature.SampleGpuTiming && _pbr is null && _feature.FrameStatistics.VisibilityCacheHits == 0
                    && (times[4] == 0 || times[5] == 0))
                    throw new InvalidOperationException("Rendering did not write raster timestamps.");
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
        graph.UseTexture(s_Depth, new("cache-check-depth", RenderGraphTextureFormat.Depth32Float, owner._renderSize, owner._renderSize));
        if (owner._pbr is not null) owner._pbr.BuildRenderGraph(ref graph, in owner._context);
        else owner._feature.BuildRenderGraph(ref graph, in owner._context);
        graph.UseImportedBuffer(s_Readback, new("cache-check-pixels", Wgpu.GetBufferSize(owner._pixels.GetWgpu<WGPUBuffer>()),
            RenderGraphBufferUsage.CopyDestination | RenderGraphBufferUsage.MapRead));
        graph.BindImportedBuffer(s_Readback, owner._pixels.GetWgpu<WGPUBuffer>());
        graph.ExportBuffer(s_Readback, RenderGraphBufferUsage.MapRead);
        graph.UseComputePass(new("cache-check-copy"), "cache-check-copy", declaration => {
            declaration.Read(s_Color, RenderGraphTextureUsage.CopySource).Write(s_Readback, RenderGraphBufferUsage.CopyDestination);
            if (owner._timingEnabled && owner._feature.SampleGpuTiming) declaration.Read(owner._feature.GpuTimingsTarget, RenderGraphBufferUsage.CopySource);
        }, context => {
            Wgpu.CopyTextureToBuffer(context.CommandEncoder, context.GetTexture(s_Color), context.GetBuffer(s_Readback), 128, 128, 512);
            if (owner._timingEnabled && owner._feature.SampleGpuTiming) Wgpu.CopyBufferToBuffer(context.CommandEncoder, context.GetBuffer(owner._feature.GpuTimingsTarget), 0,
                context.GetBuffer(s_Readback), 128 * 128 * 4, (ulong)VisibilityPbrFeature.GpuTimingStages.Length * 16);
        });
        return Sia.Reactive.Reactive.None;
    }

    public static async Task RunAsync()
    {
        using var gpu = await GpuDevice.CreateAsync(true);
        Console.WriteLine($"Visibility verification adapter: {gpu.Description}; timestamps={gpu.TimingEnabled}.");
        var mesh = MeshPatchAsset.Cook(Assets.Grid(16));
        var transformed = PbrSceneAsset.Create([mesh], [new(PbrMaterial.Default, DoubleSided: true)],
            [new(0, 0, float4x4.Scale(new float3(.6f, .6f, 1))), new(0, 0, float4x4.Translate(new(.3f, 0, .2f)))]);
        var worldSpace = PbrSceneAsset.Create([mesh], [new(PbrMaterial.Default, DoubleSided: true)], [new(0, 0, float4x4.identity)]);
        foreach (var lod in new[] { false, true })
        foreach (var asset in new[] { transformed, worldSpace }) {
            using var untimed = new VisibilityCacheVerification(gpu, asset, lod: lod, timing: false);
            using var timed = new VisibilityCacheVerification(gpu, asset, lod: lod);
            foreach (var shear in new[] { 0f, .8f, .8f, -.8f, 0f }) {
                var projection = float4x4.identity; projection.c2.x = shear;
                var expected = await untimed.FrameAsync(projection);
                var actual = await timed.FrameAsync(projection);
                if (!expected.AsSpan().SequenceEqual(actual)) throw new InvalidOperationException("GPU timing changed raster output.");
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
        foreach (var lod in new[] { false, true }) {
            using var scaled = new VisibilityCacheVerification(gpu, worldSpace, renderSize: 64, lod: lod);
            var pixels = await scaled.FrameAsync(float4x4.identity);
            if (pixels.Where((_, i) => i % 4 != 3).All(value => value == 0))
                throw new InvalidOperationException("Scaled frame is empty.");
            for (var y = 0; y < 128; y += 2) for (var x = 0; x < 128; x += 2) {
                var p = (y * 128 + x) * 4;
                if (!pixels.AsSpan(p, 4).SequenceEqual(pixels.AsSpan(p + 4, 4))
                    || !pixels.AsSpan(p, 8).SequenceEqual(pixels.AsSpan(p + 512, 8)))
                    throw new InvalidOperationException("Half-resolution output must upscale each source pixel to a 2x2 block.");
            }
        }
        foreach (var lod in new[] { false, true }) {
            using var resizing = new VisibilityCacheVerification(gpu, transformed, lod: lod);
            foreach (var size in new uint[] { 128, 64, 64, 96, 128 }) {
                using var fresh = new VisibilityCacheVerification(gpu, transformed, renderSize: size, lod: lod);
                var expected = await fresh.FrameAsync(float4x4.identity);
                var actual = await resizing.FrameAsync(float4x4.identity, size);
                if (!expected.AsSpan().SequenceEqual(actual)) throw new InvalidOperationException("Live resolution change differs from fresh rendering.");
            }
        }
        Console.WriteLine("Visibility verification passed: timed/untimed raster, cached/fresh frames, camera changes, fixed/GPU LOD live resizing and timestamps.");
    }

    public void Dispose() => GpuDevice.DisposeAll(() => _mount?.Unmount(), _graphWorld.Dispose, _render.Dispose, _main.Dispose);
}
