using System.Diagnostics;
using Sia.GLFW;
using Sia.Input;
using Sia.WebGPU;
using Sia.Window;

namespace Sia.Engine.Example;

internal sealed unsafe partial class SceneExampleApp : IDisposable
{
    private const int _initialWidth = 1280;
    private const int _initialHeight = 720;

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

    public void Run()
    {
        Initialize();

        Console.WriteLine("Sia.Graphics scene example - Esc to close.");

        var clock = Stopwatch.StartNew();
        var previousTime = clock.Elapsed.TotalSeconds;

        while (!Glfw.ShouldClose(_window)) {
            Glfw.PollEvents();

            var currentTime = clock.Elapsed.TotalSeconds;
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
        }
    }

    private void Initialize()
    {
        Glfw.Initialize();
        _glfwInitialized = true;
        _window = Glfw.CreateWindow(
            new WindowDescriptor(
                _initialWidth,
                _initialHeight,
                "Sia.Graphics - Scene Example",
                Resizable: true),
            new GlfwWindowOptions(ClientApi.NoApi));

        _instance = Wgpu.CreateInstance();
        _surface = CreateSurface(_instance, _window);

        var adapterOptions = BuildAdapterOptions();
        _adapter = Wgpu.RequestAdapter(_instance, in adapterOptions);

        var surfaceInfo = GetSurfaceInfo(_surface, _adapter);
        _surfaceFormat = surfaceInfo.Format;
        _alphaMode = surfaceInfo.AlphaMode;
        _presentMode = surfaceInfo.PresentMode;

        _device = Wgpu.RequestDevice(_adapter);
        _queue = Wgpu.GetQueue(_device);

        InitializeRenderGraph();
        InitializeScene();
        ResizeIfNeeded(force: true);
        UpdateScene(0f);
        RenderFrame();
    }

    private WGPURequestAdapterOptions BuildAdapterOptions() => new() {
        NextInChain = null,
        FeatureLevel = WGPUFeatureLevel.Core,
        PowerPreference = WGPUPowerPreference.HighPerformance,
        ForceFallbackAdapter = 0,
        BackendType = WGPUBackendType.Undefined,
        CompatibleSurface = Pointer(_surface),
    };

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
            if ((capabilities.Usages & WGPUTextureUsage.RenderAttachment) == 0) {
                throw new WgpuException("The selected surface cannot be used as a render attachment.");
            }
            if (capabilities.FormatCount == 0) {
                throw new WgpuException("The selected surface exposes no texture formats.");
            }

            var format = PickSurfaceFormat(in capabilities);
            var alphaMode = PickAlphaMode(in capabilities);
            var presentMode = PickPresentMode(in capabilities);
            return new SurfaceInfo(format, alphaMode, presentMode);
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
        Wgpu.ConfigureSurface(_surface, in configuration);
        _surfaceConfigured = true;

        OnFramebufferResized();
        return true;
    }

    private void RenderFrame()
    {
        var surfaceTexture = Wgpu.AcquireSurfaceTexture(_surface);
        if (surfaceTexture.Status is not (
            WGPUSurfaceGetCurrentTextureStatus.SuccessOptimal
            or WGPUSurfaceGetCurrentTextureStatus.SuccessSuboptimal)) {
            if (surfaceTexture.HasTexture) {
                Wgpu.Release(ref surfaceTexture);
            }

            if (surfaceTexture.Status is WGPUSurfaceGetCurrentTextureStatus.Outdated
                or WGPUSurfaceGetCurrentTextureStatus.Lost) {
                ResizeIfNeeded(force: true);
                return;
            }
            if (surfaceTexture.Status == WGPUSurfaceGetCurrentTextureStatus.Timeout) {
                return;
            }

            throw new WgpuException($"Surface texture acquisition failed with status {surfaceTexture.Status}.");
        }

        try {
            UpdateRenderGraph(surfaceTexture.Texture);
            ExecuteRenderGraph();
            Wgpu.PresentSurfaceOrThrow(_surface);
        }
        finally {
            Wgpu.Release(ref surfaceTexture);
        }
    }

    public void Dispose()
    {
        if (_disposed) {
            return;
        }
        _disposed = true;

        DisposeRenderGraph();
        DisposeScene();

        Wgpu.Release(ref _queue);

        if (!_surface.IsNull && _surfaceConfigured) {
            Wgpu.UnconfigureSurface(_surface);
            _surfaceConfigured = false;
        }
        if (!_device.IsNull) {
            Wgpu.DestroyDevice(_device);
        }
        Wgpu.Release(ref _device);
        Wgpu.Release(ref _adapter);
        Wgpu.Release(ref _surface);
        Wgpu.Release(ref _instance);

        if (!_window.IsNull) {
            Glfw.DestroyWindow(ref _window);
        }
        if (_glfwInitialized) {
            Glfw.Terminate();
            _glfwInitialized = false;
        }
    }

    private static T* Pointer<T>(WgpuHandle<T> handle)
        where T : unmanaged => (T*)handle.DangerousGetHandle();

    private readonly record struct SurfaceInfo(
        WGPUTextureFormat Format,
        WGPUCompositeAlphaMode AlphaMode,
        WGPUPresentMode PresentMode);
}
