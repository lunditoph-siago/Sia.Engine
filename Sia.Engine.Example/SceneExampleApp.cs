using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
using Sia.Engine.Mesh;
using Sia.Engine.Rendering.Pbr;
using Sia.Math;
using Sia.GLFW;
using Sia.Input;
using Sia.WebGPU;
using Sia.Window;

namespace Sia.Engine.Example;

internal sealed unsafe partial class SceneExampleApp : IDisposable
{
    private const int InitialWidth = 1280, InitialHeight = 720;
    private GlfwWindow _window;
    private WgpuHandle<WGPUInstance> _instance;
    private WgpuHandle<WGPUSurface> _surface;
    private WgpuHandle<WGPUAdapter> _adapter;
    private WgpuHandle<WGPUDevice> _device;
    private WgpuHandle<WGPUQueue> _queue;
    private WGPUTextureFormat _surfaceFormat;
    private WGPUCompositeAlphaMode _alphaMode;
    private WGPUPresentMode _presentMode;
    private int _framebufferWidth;
    private int _framebufferHeight;
    private bool _glfwInitialized;
    private bool _surfaceConfigured;
    private bool _disposed;
    private bool _disposing;
    private bool _shutdownComplete;
    private bool _deviceDestroyed;
    private readonly ScenePipeline _pipeline;

    private readonly SceneOptions _options;

    public SceneExampleApp(SceneOptions options, PbrSceneFiles.Prepared? references, LightingFiles.Prepared lighting, LightmapFiles.Prepared? lightmaps,
        ReflectionCaptureFiles.Prepared[] captures)
    {
        _options = options;
        _pipeline = options.Pipeline;
        _referenceScene = references;
        _authoredCamera = SelectAuthoredCamera(options.Camera, references?.Scene.Data);
        _preparedLighting = lighting;
        _preparedLightmaps = lightmaps;
        _preparedCaptures = captures;
        _bunnyDebugMode = _pipeline == ScenePipeline.Bunny
            ? VisibilityDebugMode.Triangles : VisibilityDebugMode.Shaded;
        _bunnyDistance = 0;
        _bunnyTour = _pipeline == ScenePipeline.Bunny && _authoredCamera is null;
    }

#if !BROWSER
    public void Run()
    {
        Initialize();

        Console.WriteLine($"Sia.Engine {_pipeline} scene example - Esc to close.");

        var previousTime = Environment.TickCount64 / 1000.0;

        while (!Glfw.ShouldClose(_window)) {
            ThrowGpuError();
            Glfw.PollEvents();

            var currentTime = Environment.TickCount64 / 1000.0;
            var deltaTime = (float)System.Math.Min(currentTime - previousTime, 0.1);
            previousTime = currentTime;

            if (Glfw.GetKey(_window, Key.Escape) != InputAction.Release) {
                Glfw.RequestClose(_window);
            }

            if (!ResizeIfNeeded()) {
                Thread.Sleep(16);
                continue;
            }

            UpdateScene(deltaTime);
            RenderFrame();
            Wgpu.ProcessEvents(_instance);
            ThrowGpuError();
        }
    }

    private void Initialize()
    {
        Glfw.Initialize();
        _glfwInitialized = true;
        _window = Glfw.CreateWindow(
            new WindowDescriptor(
                InitialWidth,
                InitialHeight,
                $"Sia.Engine - {_pipeline} Example",
                Resizable: true),
            new GlfwWindowOptions(ClientApi.NoApi));

        _instance = Wgpu.CreateInstance();
        _surface = CreateSurface(_instance, _window);

        var adapterOptions = BuildAdapterOptions();
        _adapter = Wgpu.RequestAdapter(_instance, in adapterOptions);
        ReadAdapterDescription();

        var surfaceInfo = GetSurfaceInfo(_surface, _adapter);
        _surfaceFormat = surfaceInfo.Format;
        _alphaMode = surfaceInfo.AlphaMode;
        _presentMode = surfaceInfo.PresentMode;
        _presentationSurfaceUsages = surfaceInfo.Usages;

        var required = WGPULimits.Default;
        ConfigureSceneLimits(ref required);
        var deviceDescriptor = CreateDeviceDescriptor();
        deviceDescriptor.RequiredLimits = &required;
        var requiredFeatures = GetRequiredDeviceFeatures();
        fixed (WGPUFeatureName* features = requiredFeatures) {
            deviceDescriptor.RequiredFeatureCount = (nuint)requiredFeatures.Length;
            deviceDescriptor.RequiredFeatures = features;
            _device = Wgpu.RequestDevice(_adapter, deviceDescriptor);
        }
        _queue = Wgpu.GetQueue(_device);

        InitializeRenderGraph();
        InitializeScene();
        ResizeIfNeeded(force: true);
        UpdateScene(0f);
        RenderFrame();
    }
#endif

    private WGPURequestAdapterOptions BuildAdapterOptions(
        WGPUFeatureLevel featureLevel = WGPUFeatureLevel.Core,
        WGPUPowerPreference powerPreference = WGPUPowerPreference.HighPerformance) => new() {
            NextInChain = null,
            FeatureLevel = featureLevel,
            PowerPreference = powerPreference,
            ForceFallbackAdapter = 0,
            BackendType = WGPUBackendType.Undefined,
            CompatibleSurface = Pointer(_surface),
        };

    private WGPUDeviceDescriptor CreateDeviceDescriptor()
    {
        var descriptor = WGPUDeviceDescriptor.Default;
#if BROWSER
        descriptor.UncapturedErrorCallbackInfo.Callback =
            (delegate* unmanaged[Cdecl]<WGPUDevice**, WGPUErrorType, WGPUStringView, void*, void*, void>)
            (delegate* unmanaged[Cdecl]<WGPUDevice**, WGPUErrorType, WGPUStringView*, void*, void*, void>)&OnBrowserGpuError;
#else
        descriptor.UncapturedErrorCallbackInfo.Callback = &OnGpuError;
#endif
        descriptor.UncapturedErrorCallbackInfo.Userdata1 = (void*)_gpuSession.Generation;
        descriptor.DeviceLostCallbackInfo.Userdata1 = (void*)_gpuSession.Generation;
        descriptor.DeviceLostCallbackInfo.Mode = WGPUCallbackMode.AllowSpontaneous;
#if BROWSER
        descriptor.DeviceLostCallbackInfo.Callback =
            (delegate* unmanaged[Cdecl]<WGPUDevice**, WGPUDeviceLostReason, WGPUStringView, void*, void*, void>)
            (delegate* unmanaged[Cdecl]<WGPUDevice**, WGPUDeviceLostReason, WGPUStringView*, void*, void*, void>)&OnBrowserDeviceLost;
#else
        descriptor.DeviceLostCallbackInfo.Callback = &OnDeviceLost;
#endif
        return descriptor;
    }

    private bool RequiresBlockTextures()
    {
        if (_referenceScene is not { } scene || !scene.Textures.Values.Any(texture => texture.Data.Format != PbrTextureFormat.Rgba8))
            return false;
        if (WgpuUnsafe.wgpuAdapterHasFeature(Pointer(_adapter), WGPUFeatureName.TextureCompressionBC) == 0)
            throw new NotSupportedException("This content package requires BC texture compression. Select an rgba8 package for an adapter without texture-compression-bc.");
        if (scene is PbrSceneFiles.PreparedMixed or PbrSceneFiles.PreparedRasterStreaming
            && scene.Textures.Values.Any(texture => texture.Data.Format != PbrTextureFormat.Rgba8 && texture.Mips is { TailMip: > 0 })
            && WgpuUnsafe.wgpuAdapterHasFeature(Pointer(_adapter), WGPUFeatureName.CoreFeaturesAndLimits) == 0)
            throw new NotSupportedException("This content package requires WebGPU core for block-compressed mip streaming. Select an rgba8 package for this adapter.");
        return true;
    }

    private WGPUFeatureName[] GetRequiredDeviceFeatures()
    {
        var features = new List<WGPUFeatureName>(2);
        if (RequiresBlockTextures()) features.Add(WGPUFeatureName.TextureCompressionBC);
        if (Wgpu.HasFeature(_adapter, WGPUFeatureName.Float32Blendable))
            features.Add(WGPUFeatureName.Float32Blendable);
        return [.. features];
    }

    private void ConfigureSceneLimits(ref WGPULimits required)
    {
        if (_pipeline == ScenePipeline.Unlit) {
            return;
        }
        var supported = WGPULimits.Default;
        if (WgpuUnsafe.wgpuAdapterGetLimits((WGPUAdapter*)_adapter.DangerousGetHandle(), &supported) != WGPUStatus.Success) {
            throw new WgpuException("The adapter did not report its geometry buffer limits.");
        }
        required.MaxStorageBufferBindingSize = System.Math.Min(supported.MaxStorageBufferBindingSize, 256ul * 1024 * 1024);
        required.MaxBufferSize = System.Math.Min(supported.MaxBufferSize, 256ul * 1024 * 1024);
        var preset = PbrRendererSettings.ForQuality(_options.Quality);
        var probes = preset.DynamicSceneGi;
        var textures = 10u + (probes ? 1u : 0u) + (_preparedLightmaps is not null ? 3u : 0u) + (_preparedCaptures is { Length: > 0 } ? 1u : 0u);
        if (supported.MaxSampledTexturesPerShaderStage < textures || supported.MaxVertexBuffers < 4 || supported.MaxVertexAttributes < 4)
            throw new NotSupportedException("The selected PBR lighting requires more sampled textures or vertex attributes than this adapter provides.");
        required.MaxSampledTexturesPerShaderStage = textures;
        required.MaxVertexBuffers = 4; required.MaxVertexAttributes = 4;
        if (preset.Reflections is not null) {
            if (supported.MaxStorageTexturesPerShaderStage < 4) throw new NotSupportedException("Live reflections require four compute storage textures.");
            required.MaxStorageTexturesPerShaderStage = 4;
        }
    }

#if BROWSER
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnBrowserGpuError(WGPUDevice** device, WGPUErrorType type, WGPUStringView* message, void* userdata1, void* userdata2)
    {
        ReportGpuError(type, *message, (nint)userdata1);
    }
#else
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnGpuError(WGPUDevice** device, WGPUErrorType type, WGPUStringView message, void* userdata1, void* userdata2)
    {
        ReportGpuError(type, message, (nint)userdata1);
    }
#endif

#if BROWSER
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnBrowserDeviceLost(WGPUDevice** device, WGPUDeviceLostReason reason, WGPUStringView* message, void* userdata1, void* userdata2)
        => ReportDeviceLost(reason, *message, (nint)userdata1);
#else
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnDeviceLost(WGPUDevice** device, WGPUDeviceLostReason reason, WGPUStringView message, void* userdata1, void* userdata2)
        => ReportDeviceLost(reason, message, (nint)userdata1);
#endif

    private static void ReportDeviceLost(WGPUDeviceLostReason reason, WGPUStringView message, nint generation)
    {
        var session = FindGpuErrorSession(generation);
        if (session is null) return;
        // No native calls or exception propagation from this spontaneous callback.
        try {
            var timestamp = Stopwatch.GetTimestamp();
            var utc = DateTimeOffset.UtcNow;
            var length = checked((int)System.Math.Min((ulong)message.Length, 16ul * 1024));
            var text = message.Data == null ? string.Empty : Marshal.PtrToStringUTF8((nint)message.Data, length) ?? string.Empty;
            var loss = session.TryRecordLoss(reason.ToString(), reason == WGPUDeviceLostReason.Destroyed, text, utc, timestamp);
            if (loss is null) return;
            var error = $"WebGPU device lost ({loss.Reason}), session {loss.SessionGeneration}, at {loss.Utc:O}, stopwatch {loss.StopwatchTimestamp}: {loss.Message}";
            session.TryRecordError(error);
            try { Console.Error.WriteLine(error); } catch { }
        }
        catch { session.TryRecordError("WebGPU device lost; native loss diagnostics were unavailable."); }
    }

    private static void ReportGpuError(WGPUErrorType type, WGPUStringView message, nint generation)
    {
        var session = FindGpuErrorSession(generation);
        if (session is null) return;
        try {
            var length = checked((int)System.Math.Min((ulong)message.Length, 16ul * 1024));
            var text = message.Data == null ? string.Empty : Marshal.PtrToStringUTF8((nint)message.Data, length) ?? string.Empty;
            var error = $"WebGPU {type}: {text}";
            if (session.TryRecordError(error)) {
                try { Console.Error.WriteLine(error); } catch { }
            }
        }
        catch { session.TryRecordError("WebGPU uncaptured error; native diagnostics were unavailable."); }
    }

    private static void ThrowGpuError()
    {
        if (Volatile.Read(ref _gpuCurrentSession)?.Error is { } error)
            throw new WgpuException(error);
    }

    private static WgpuHandle<WGPUSurface> CreateSurface(
        WgpuHandle<WGPUInstance> instance,
        GlfwWindow window)
    {
#if BROWSER
        return Wgpu.CreateCanvasSurface(instance, "#canvas", "Sia.Engine scene surface");
#else
        if (OperatingSystem.IsWindows()) {
            return Wgpu.CreateWindowsSurface(
                instance,
                GlfwPlatformNative.GetCurrentWin32ModuleHandle(),
                Glfw.GetWin32Window(window),
                "Scene example surface");
        }

        if (OperatingSystem.IsLinux()) {
            var waylandDisplay = Glfw.GetWaylandDisplay();
            if (waylandDisplay != 0) {
                return Wgpu.CreateWaylandSurface(
                    instance,
                    waylandDisplay,
                    Glfw.GetWaylandWindow(window),
                    "Scene example surface");
            }

            return Wgpu.CreateXlibSurface(
                instance,
                Glfw.GetX11Display(),
                (ulong)Glfw.GetX11Window(window),
                "Scene example surface");
        }

        throw new PlatformNotSupportedException(
            "This example currently creates WebGPU surfaces for Win32, X11, and Wayland.");
#endif
    }

    private static SurfaceInfo GetSurfaceInfo(
        WgpuHandle<WGPUSurface> surface,
        WgpuHandle<WGPUAdapter> adapter)
    {
        var capabilities = default(WGPUSurfaceCapabilities);
        var status = WgpuUnsafe.wgpuSurfaceGetCapabilities(
            Pointer(surface),
            Pointer(adapter),
            &capabilities);
        if (status != WGPUStatus.Success) {
            throw new WgpuException($"Surface capability query failed with status {status}.");
        }

        try {
#if !BROWSER
            if ((capabilities.Usages & WGPUTextureUsage.RenderAttachment) == 0) {
                throw new WgpuException("The selected surface cannot be used as a render attachment.");
            }
#endif
            if (capabilities.FormatCount == 0) {
                throw new WgpuException("The selected surface exposes no texture formats.");
            }

            var format = PickSurfaceFormat(in capabilities);
            var alphaMode = PickAlphaMode(in capabilities);
            var presentMode = PickPresentMode(in capabilities);
            return new SurfaceInfo(format, alphaMode, presentMode, capabilities.Usages);
        }
        finally {
            WgpuUnsafe.wgpuSurfaceCapabilitiesFreeMembers(capabilities);
        }
    }

    private static WGPUTextureFormat PickSurfaceFormat(in WGPUSurfaceCapabilities capabilities)
    {
        WGPUTextureFormat[] preferredFormats = [
            WGPUTextureFormat.BGRA8Unorm,
            WGPUTextureFormat.RGBA8Unorm,
            WGPUTextureFormat.BGRA8UnormSrgb,
            WGPUTextureFormat.RGBA8UnormSrgb,
        ];

        foreach (var preferred in preferredFormats) {
            for (nuint index = 0; index < capabilities.FormatCount; index++) {
                if (capabilities.Formats[index] == preferred) {
                    return preferred;
                }
            }
        }

        return capabilities.Formats[0];
    }

    private static WGPUCompositeAlphaMode PickAlphaMode(in WGPUSurfaceCapabilities capabilities)
    {
        for (nuint index = 0; index < capabilities.AlphaModeCount; index++) {
            if (capabilities.AlphaModes[index] == WGPUCompositeAlphaMode.Opaque) {
                return WGPUCompositeAlphaMode.Opaque;
            }
        }

        return capabilities.AlphaModeCount == 0
            ? WGPUCompositeAlphaMode.Auto
            : capabilities.AlphaModes[0];
    }

    private static WGPUPresentMode PickPresentMode(in WGPUSurfaceCapabilities capabilities)
    {
        for (nuint index = 0; index < capabilities.PresentModeCount; index++) {
            if (capabilities.PresentModes[index] == WGPUPresentMode.Fifo) {
                return WGPUPresentMode.Fifo;
            }
        }

        return capabilities.PresentModeCount == 0
            ? WGPUPresentMode.Fifo
            : capabilities.PresentModes[0];
    }

    private bool ResizeIfNeeded(bool force = false)
    {
        var size = Glfw.GetFramebufferSize(_window);
        if (size.Width <= 0 || size.Height <= 0) {
            return false;
        }
        if (!force && size.Width == _framebufferWidth && size.Height == _framebufferHeight) {
            return true;
        }

        _framebufferWidth = size.Width;
        _framebufferHeight = size.Height;

        var configuration = new WGPUSurfaceConfiguration {
            NextInChain = null,
            Device = Pointer(_device),
            Format = _surfaceFormat,
            Usage = WGPUTextureUsage.RenderAttachment,
            Width = (uint)_framebufferWidth,
            Height = (uint)_framebufferHeight,
            ViewFormatCount = 0,
            ViewFormats = null,
            AlphaMode = _alphaMode,
            PresentMode = _presentMode,
        };
#if !BROWSER
        if (UsePresentationCopy) configuration.Usage |= WGPUTextureUsage.CopyDst;
#endif
        Wgpu.ConfigureSurface(_surface, in configuration);
        _surfaceConfigured = true;

        OnFramebufferResized();
        return true;
    }

    private void RenderFrame()
    {
        ThrowGpuError();
#if !BROWSER
        if (UsePresentationCopy) {
            RenderPresentationCopyFrame();
            return;
        }
#endif
        RenderFrameDirect();
    }

    private void RenderFrameDirect()
    {
        ThrowGpuError();
        var surfaceTexture = Wgpu.AcquireSurfaceTexture(_surface);
        try {
            ThrowGpuError();
            if (surfaceTexture.Status is not (
                WGPUSurfaceGetCurrentTextureStatus.SuccessOptimal
                or WGPUSurfaceGetCurrentTextureStatus.SuccessSuboptimal)) {
                if (surfaceTexture.Status is WGPUSurfaceGetCurrentTextureStatus.Outdated
                    or WGPUSurfaceGetCurrentTextureStatus.Lost) {
                    ResizeIfNeeded(force: true);
                    return;
                }
                if (surfaceTexture.Status == WGPUSurfaceGetCurrentTextureStatus.Timeout) return;
                throw new WgpuException($"Surface texture acquisition failed with status {surfaceTexture.Status}.");
            }
            UpdateRenderGraph(surfaceTexture.Texture);
            ExecuteRenderGraph();
            ThrowGpuError();
#if !BROWSER
            Wgpu.PresentSurfaceOrThrow(_surface);
            ThrowGpuError();
#endif
        }
        finally { Wgpu.Release(ref surfaceTexture); }
    }

#if !BROWSER
    // Native queue callbacks may require instance event processing while shutdown joins.
    // Keep both event processing and subsequent disposal on the graphics owner thread.
    public void StopSceneStreaming()
    {
        var stop = StopSceneStreamingAsync().AsTask();
        while (!stop.IsCompleted) {
            Wgpu.ProcessEvents(_instance);
            Thread.Sleep(2);
        }
        stop.GetAwaiter().GetResult();
    }
#endif
    public void Dispose()
    {
        if (_disposed) return;
        if (_disposing) throw new InvalidOperationException("Scene disposal is already in progress.");
        _disposing = true;
        var errors = new List<Exception>();
        bool Attempt(Action cleanup)
        {
            try { cleanup(); return true; }
            catch (Exception error) { errors.Add(error); return false; }
        }
        void ThrowFailures()
        {
            if (errors.Count == 1) ExceptionDispatchInfo.Capture(errors[0]).Throw();
            if (errors.Count != 0) throw new AggregateException("Scene cleanup did not complete.", errors);
        }

        try {
            // These simulation-only owners have no pending GPU work. Other stages
            // remain retained until streaming and queue completion are confirmed.
            Attempt(() => { _sceneStage?.Dispose(); _sceneStage = null; });
            Attempt(() => { _sceneWorld?.Dispose(); _sceneWorld = null; });
            if (!_queue.IsNull && !_shutdownComplete) {
                errors.Add(new InvalidOperationException("Complete scene streaming and GPU queue shutdown before disposing graphics resources."));
                ThrowFailures();
            }

            if (!Attempt(() => { _sceneRenderer?.Dispose(); _sceneRenderer = null; })) {
                ThrowFailures();
            }
            var graphDisposed = Attempt(DisposeRenderGraph);
            if (!graphDisposed) ThrowFailures();
            var sceneDisposed = Attempt(DisposeScene);
            if (!sceneDisposed) ThrowFailures();

            Attempt(() => Wgpu.Release(ref _queue));
            Attempt(() => {
                if (!_surface.IsNull && _surfaceConfigured) {
                    Wgpu.UnconfigureSurface(_surface);
                    _surfaceConfigured = false;
                }
            });
            // Unconfigure can retain a backend swapchain. Release it before
            // explicit device destruction while the backend deleter is alive.
            if (!_surfaceConfigured) Attempt(() => Wgpu.Release(ref _surface));
            if (_queue.IsNull && !_surfaceConfigured && _surface.IsNull) {
                if (!_device.IsNull && !_deviceDestroyed)
                    Attempt(() => { _gpuSession.ExpectDestroy(); Wgpu.DestroyDevice(_device); _deviceDestroyed = true; });
                if (_device.IsNull || _deviceDestroyed)
                    Attempt(() => Wgpu.Release(ref _device));
            }
            if (_device.IsNull) Attempt(() => Wgpu.Release(ref _adapter));
            if (_queue.IsNull && _device.IsNull && _adapter.IsNull && _surface.IsNull)
                Attempt(() => Wgpu.Release(ref _instance));

            if (_surface.IsNull) Attempt(() => {
                if (!_window.IsNull) Glfw.DestroyWindow(ref _window);
            });
            if (_window.IsNull) Attempt(() => {
                if (_glfwInitialized) {
                    Glfw.Terminate();
                    _glfwInitialized = false;
                }
            });

            ThrowFailures();
            _disposed = true;
        }
        finally { _disposing = false; }
    }

    private static T* Pointer<T>(WgpuHandle<T> handle)
        where T : unmanaged => (T*)handle.DangerousGetHandle();

    private readonly record struct SurfaceInfo(
        WGPUTextureFormat Format,
        WGPUCompositeAlphaMode AlphaMode,
        WGPUPresentMode PresentMode,
        WGPUTextureUsage Usages);

    private void ReadAdapterDescription()
    {
        var info = WGPUAdapterInfo.Default;
        if (WgpuUnsafe.wgpuAdapterGetInfo(Pointer(_adapter), &info) != WGPUStatus.Success)
            return;
        try {
#if !BROWSER
            _presentationBackend = info.BackendType;
#endif
            static string Text(WGPUStringView text) => Marshal.PtrToStringUTF8((nint)text.Data, checked((int)text.Length)) ?? "";
            Console.WriteLine($"Adapter: {Text(info.Vendor)}; {Text(info.Device)}; {Text(info.Description)}; {info.BackendType}");
        }
        finally { WgpuUnsafe.wgpuAdapterInfoFreeMembers(info); }
    }
}
