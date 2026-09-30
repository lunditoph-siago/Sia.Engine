using Sia;
using Sia.Engine.Camera;
using Sia.Math;
using Sia.Graphics.Reactive;
using Sia.WebGPU;

namespace Sia.Engine.Rendering.Pbr;

public readonly record struct PbrGpuTraversalStatistics(
    bool Enabled,
    ulong AllocatedBytes,
    int PendingReadbacks,
    long DroppedReadbacks,
    long FailedReadbacks,
    long MaximumFeedbackAge,
    uint DeferredRefinements);

public enum PbrOpaquePath
{
    Visibility,
    ForwardPlus
}

public sealed record PbrRendererSettings
{
    public PbrOpaquePath OpaquePath { get; init; } = PbrOpaquePath.Visibility;
    public bool ExportSurfaceData { get; init; }
    public bool GpuTiming { get; init; }

    public float TargetPixelError { get; init; }
    public float ShadowTexelError { get; init; }
    public uint ShadowResolution { get; init; } = 512;

    public ulong GeometryBytes { get; init; } = 256ul * 1024 * 1024;
    public ulong MaterialBytes { get; init; } = 128ul * 1024 * 1024;
    public ulong ViewBytes { get; init; } = 256ul * 1024 * 1024;
    public PbrStreamingSettings Streaming { get; init; } = new();
    public PbrTextureStreamingSettings TextureStreaming { get; init; } = new();

    public IblEnvironmentAsset? BakedEnvironment { get; init; }
    public DiffuseProbeAsset? BakedProbes { get; init; }
    public bool DynamicSceneGi { get; init; }
    public uint ProbeUpdates { get; init; } = 4;
    public uint ProbeSamples { get; init; } = 64;
    public uint3 ProbeDimensions { get; init; } = new(4, 4, 4);
    public Aabb? ProbeBounds { get; init; }
    public ulong SceneGiBytes { get; init; } = 160ul * 1024 * 1024;

    public static PbrRendererSettings ForQuality(RenderQuality quality) => quality switch {
        RenderQuality.Low => new() {
            OpaquePath = PbrOpaquePath.Visibility,
            TargetPixelError = .5f,
            ShadowTexelError = .5f,
            ShadowResolution = 256,
            Streaming = new() { MaximumSelectionNodesPerView = 1024 }
        },
        RenderQuality.Medium => new() {
            OpaquePath = PbrOpaquePath.Visibility,
            TargetPixelError = .25f,
            ShadowTexelError = .25f,
            ShadowResolution = 512
        },
        RenderQuality.High => new() {
            OpaquePath = PbrOpaquePath.Visibility,
            DynamicSceneGi = true,
            TargetPixelError = 0,
            ShadowTexelError = 0,
            ShadowResolution = 1024
        },
        _ => throw new ArgumentOutOfRangeException(nameof(quality))
    };
}

public readonly record struct PbrFrameStatistics(
    uint Triangles,
    int MaterialBatches,
    ulong SceneBytes,
    ulong ViewBytes);

public sealed class PbrRenderer :
    IPrepareRenderFeature<RenderFrameContext>,
    IRenderGraphContributor<RenderFrameContext>,
    IDisposable
{
    private static readonly string[] s_TimingStages = ["pbr-frame", "clusters", "shadows", "opaque", "probe-update", "material-lighting", "transparency", "output"];

    public static ReadOnlySpan<string> GpuTimingStages => s_TimingStages;

    private readonly List<PbrView> _views = [];
    private readonly HashSet<RenderView> _preparedViews = [];
    private bool _disposed;
    private RenderWorld? _streamWorld;
    private ulong _streamFrame;

    public RenderFeatureKey Key { get; } = new("pbr");
    public VisibilityDebugMode DebugMode { get; set; }
    public bool SampleGpuTiming { get; set; } = true;

    public PbrFrameStatistics FrameStatistics { get; private set; }
    public Aabb Bounds => Scene.Bounds;
    public PbrStreamingStatistics? StreamingStatistics => Scene.Streaming?.Statistics;
    public PbrTextureStreamingStatistics TextureStreamingStatistics => Materials.Statistics;
    public ulong ResourceRevision => Materials.Revision;
    public PbrGpuTraversalStatistics GpuTraversalStatistics {
        get {
            var bytes = Scene.Streaming?.Hierarchy?.Bytes ?? 0;
            var pending = 0;
            long dropped = 0, failed = 0, age = -1;
            uint deferred = 0;
            foreach (var view in _views) {
                if (view.Selection is not { } selection) continue;
                bytes = checked(bytes + selection.Bytes);
                pending = checked(pending + selection.PendingReadbacks);
                dropped = checked(dropped + selection.FeedbackDropped);
                failed = checked(failed + selection.FeedbackFailures);
                age = System.Math.Max(age, selection.FeedbackAge);
                deferred = checked(deferred + selection.DeferredRefinements);
            }
            return new(Scene.Streaming?.Hierarchy is not null, bytes, pending, dropped, failed, age, deferred);
        }
    }

    internal PbrPipelines Pipelines { get; }
    internal PbrGpuScene Scene { get; }
    internal PbrMaterials Materials { get; }
    internal IblEnvironmentGpu Environment { get; }
    internal DiffuseProbeGpu? Probes { get; }
    internal PbrRendererSettings Settings { get; }
    internal WGPUTextureFormat OutputFormat { get; }

    public PbrRenderer(
        in GpuFrame frame,
        PbrSceneAsset scene,
        WGPUTextureFormat outputFormat,
        PbrRendererSettings? settings = null)
        : this(frame, scene, null, outputFormat, settings) { }

    public PbrRenderer(in GpuFrame frame, PbrSceneStream scene, WGPUTextureFormat outputFormat,
        PbrRendererSettings? settings = null)
        : this(frame, scene.Bootstrap, scene, outputFormat, settings) { }

    private PbrRenderer(in GpuFrame frame, PbrSceneAsset scene, PbrSceneStream? stream,
        WGPUTextureFormat outputFormat, PbrRendererSettings? settings)
    {
        ArgumentNullException.ThrowIfNull(scene);
        Settings = settings ?? new();
        OutputFormat = outputFormat;
        if (!float.IsFinite(Settings.TargetPixelError) || Settings.TargetPixelError < 0
            || !float.IsFinite(Settings.ShadowTexelError) || Settings.ShadowTexelError < 0)
            throw new ArgumentOutOfRangeException(nameof(settings));
        if (!Enum.IsDefined(Settings.OpaquePath)) throw new ArgumentOutOfRangeException(nameof(settings));
        if (Settings.ExportSurfaceData && Settings.OpaquePath != PbrOpaquePath.Visibility)
            throw new ArgumentException("Shared surface export requires the Visibility path.", nameof(settings));
        if (Settings.DynamicSceneGi && Settings.OpaquePath != PbrOpaquePath.Visibility)
            throw new ArgumentException("Dynamic scene GI requires the Visibility path.", nameof(settings));
        if (Settings.GpuTiming && !WebGpuCapabilities.Read(frame.Device.GetWgpu<WGPUDevice>()).TimestampQueries)
            throw new NotSupportedException("GPU timing requires timestamp-query to be enabled on the device.");
        if (Settings.ShadowResolution is < 64 or > 4096) throw new ArgumentOutOfRangeException(nameof(settings));
        if (outputFormat is not (WGPUTextureFormat.RGBA8Unorm or WGPUTextureFormat.RGBA8UnormSrgb or WGPUTextureFormat.BGRA8Unorm or WGPUTextureFormat.BGRA8UnormSrgb))
            throw new ArgumentException("PBR output currently requires an SDR RGBA/BGRA surface.", nameof(outputFormat));
        try {
            var sceneGi = Settings.DynamicSceneGi || Settings.BakedProbes is not null;
            if (stream is not null && Settings.Streaming.GpuTraversal && Settings.OpaquePath != PbrOpaquePath.Visibility)
                throw new NotSupportedException("GPU stream traversal requires the Visibility path; select CPU traversal for Forward+ comparisons.");
            Pipelines = new(frame, outputFormat, Settings.ExportSurfaceData, Settings.OpaquePath, sceneGi,
                stream is not null && Settings.Streaming.GpuTraversal);
            Materials = new(frame, scene.Materials.Span, Pipelines.MaterialLayout, Settings.MaterialBytes, stream, Settings.TextureStreaming);
            Scene = stream is null ? new(frame, scene, Pipelines.GeometryLayout, Settings.GeometryBytes, Materials.MaterialBatches)
                : new(frame, stream, Pipelines.GeometryLayout, Settings.GeometryBytes, Materials.MaterialBatches, Settings.Streaming);
            Environment = new(frame, Settings.BakedEnvironment);
            if (sceneGi) {
                var tracing = Settings.DynamicSceneGi
                    ? stream is null ? PbrSceneTransport.Build(scene, System.Math.Min(Settings.SceneGiBytes, 128ul * 1024 * 1024))
                        : PbrSceneTransport.Build(stream, System.Math.Min(Settings.SceneGiBytes, 128ul * 1024 * 1024)) : null;
                var asset = Settings.BakedProbes ?? PbrSceneTransport.CreateVolume(tracing!,
                    Settings.ProbeDimensions, Settings.BakedEnvironment?.Sky ?? new ProceduralSky(), Settings.ProbeBounds);
                var identity = tracing is null ? stream is null ? PbrSceneTransport.Identity(scene) : stream.Identity.ToArray() : tracing.Identity.ToArray();
                if (!asset.SceneIdentity.Span.SequenceEqual(identity))
                    throw new ArgumentException("Baked probes belong to different scene geometry/materials.", nameof(settings));
                if (Settings.DynamicSceneGi && (Settings.ProbeUpdates == 0 || Settings.ProbeUpdates > asset.Count || Settings.ProbeSamples is < 16 or > 1024))
                    throw new ArgumentOutOfRangeException(nameof(settings), "Invalid dynamic probe update budget.");
                Probes = new(frame, asset, tracing, Settings.SceneGiBytes);
            }
        }
        catch {
            Materials?.StopAsync().GetAwaiter().GetResult();
            if (Scene?.Streaming is { } streaming) streaming.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Dispose();
            throw;
        }
    }

    public RenderGraphBufferKey GetGpuTimingsTarget(RenderViewKey view) => new("pbr/" + view.Value + "/timings");

    public VisibilitySurfaceTargets GetSurfaceTargets(RenderViewKey view, RenderGraphTextureKey depth)
    {
        if (!Settings.ExportSurfaceData)
            throw new InvalidOperationException("Surface export must be enabled before constructing the renderer.");
        var prefix = "pbr/" + view.Value + "/";
        return new(new(prefix + "id"), depth,
            new(prefix + "normal-roughness"), new(prefix + "base-metallic"));
    }

    public void Prepare(in RenderFeatureContext<RenderFrameContext> context)
        => PrepareFrame(context.RenderWorld, [context]);

    public void PrepareFrame(RenderWorld world, ReadOnlySpan<RenderFeatureContext<RenderFrameContext>> contexts)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(world);
        if (Settings.DynamicSceneGi && contexts.Length > 1)
            throw new NotSupportedException("Dynamic probes currently require one view per renderer.");
        _preparedViews.Clear();
        var views = _preparedViews;
        foreach (ref readonly var context in contexts) {
            if (!ReferenceEquals(context.RenderWorld, world) || !world.Views.Contains(context.View) || !views.Add(context.View))
                throw new ArgumentException("Prepare each active view of one render world exactly once.", nameof(contexts));
            var first = contexts[0].Frame.Frame;
            var frame = context.Frame.Frame;
            if (!ReferenceEquals(frame.MainWorld, first.MainWorld) || !ReferenceEquals(frame.ResourceWorld, first.ResourceWorld)
                || frame.Device != first.Device || frame.Queue != first.Queue)
                throw new ArgumentException("A PBR batch must share scene world, resource world, device and queue.", nameof(contexts));
        }
        _preparedViews.Clear();
        if (Scene.Streaming is not null) {
            if (_streamWorld is not null && !ReferenceEquals(_streamWorld, world))
                throw new InvalidOperationException("A streaming renderer belongs to one render world.");
            if (_streamWorld is not null && _streamFrame == world.FrameIndex)
                throw new InvalidOperationException("Use PrepareFrame with all views once per RenderWorld.BeginFrame.");
        }
        if (!contexts.IsEmpty) {
            var environment = contexts[0].Frame.Frame.MainWorld.AcquireAddon<EnvironmentLighting>();
            if (environment.Atmosphere is not null)
                throw new NotSupportedException("Atmosphere migration is not part of the resident PBR core.");
            Environment.Update(environment.Sky);
        }
        if (Scene.Streaming is not null) {
            _streamWorld = world;
            _streamFrame = world.FrameIndex;
        }
        Scene.Streaming?.BeginFrame();
        Materials.BeginFrame();
        uint triangles = 0;
        ulong viewBytes = 0;
        foreach (ref readonly var context in contexts) {
            var view = FindView(context.View, context.Frame.Frame);
            view.Prepare(context.Frame);
            triangles = checked(triangles + view.VisibleTriangles);
            viewBytes = checked(viewBytes + view.Bytes);
        }
        Scene.Streaming?.EndFrame();
        Scene.Streaming?.RefreshGpuResidency();
        Materials.EndFrame();
        FrameStatistics = new(triangles, Materials.Groups.Length,
            Scene.Bytes + Materials.Bytes + Environment.Bytes + (Probes?.Bytes ?? 0), viewBytes);
    }

    public void BuildRenderGraph(ref RenderGraphBuildContext graph, in RenderFeatureContext<RenderFrameContext> context)
        => FindView(context.View, context.Frame.Frame).Build(ref graph);

    private PbrView FindView(RenderView view, in GpuFrame frame)
    {
        foreach (var existing in _views)
            if (existing.View == view && !existing.IsDisposed) return existing;
        if (Settings.DynamicSceneGi && _views.Count != 0)
            throw new NotSupportedException("Dynamic probes currently require one view per renderer.");
        if (view.PersistentResources.TryGet<PbrView>(out var occupied) && !occupied!.IsDisposed)
            throw new InvalidOperationException("A render view may have only one PBR renderer owner.");
        var created = new PbrView(this, view, frame);
        _views.Add(created);
        view.PersistentResources.Set(created);
        return created;
    }

    internal void Forget(PbrView view) => _views.Remove(view);

    public async ValueTask StopStreamingAsync()
    {
        await Materials.StopAsync().ConfigureAwait(false);
        if (Scene.Streaming is { } streaming) await streaming.DisposeAsync().ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed) return;
        if (Scene?.Streaming is not null && !Scene.Streaming.IsStopped)
            throw new InvalidOperationException("Await StopStreamingAsync before disposing a streaming renderer.");
        if (Materials is not null && !Materials.IsStopped)
            throw new InvalidOperationException("Await StopStreamingAsync before disposing streaming materials.");
        _disposed = true;
        while (_views.Count != 0)
            _views[^1].Dispose();
        Environment?.Dispose();
        Probes?.Dispose();
        Scene?.Dispose();
        Materials?.Dispose();
        Pipelines?.Dispose();
    }
}
