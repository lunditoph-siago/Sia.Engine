using Sia;
using Sia.GLFW;
using Sia.Input;
using Sia.WebGPU;
using Sia.Window;

namespace Sia.UI.Example;

internal sealed unsafe partial class StyleShowcaseApp : IDisposable
{
    private const int InitialWidth = 1120;
    private const int InitialHeight = 720;

    private World? _windowWorld;
    private Entity? _windowEntity;
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
    private bool _surfaceConfigured;
    private bool _disposed;

    public void Run()
    {
        InitializeWindow();
        InitializeGpu();
        InitializeUi();
        ResizeIfNeeded(force: true);

        Console.WriteLine("Sia.UI style showcase - interact with the controls; Esc closes.");
        while (!Glfw.ShouldClose(_window)) {
            Glfw.PollEvents();
            if (Glfw.GetKey(_window, Key.Escape) != InputAction.Release) {
                Glfw.RequestClose(_window);
            }
            if (!ResizeIfNeeded()) {
                Thread.Sleep(16);
                continue;
            }
            UpdateUi();
            RenderFrame();
            Wgpu.ProcessEvents(_instance);
        }
    }

    private void InitializeWindow()
    {
        var world = new World();
        try {
            var entity = world.CreateGlfwWindow(
                new WindowDescriptor(
                    InitialWidth,
                    InitialHeight,
                    "Sia.UI · Reactive Presentation Showcase",
                    Resizable: true),
                new GlfwWindowOptions(ClientApi.NoApi));
            _windowWorld = world;
            _windowEntity = entity;
            _window = entity.Get<GlfwWindow>();
        }
        catch {
            world.Dispose();
            throw;
        }
    }

    private void InitializeGpu()
    {
        _instance = Wgpu.CreateInstance();
        _surface = CreateSurface(_instance, _window);
        _adapter = Wgpu.RequestAdapter(_instance, BuildAdapterOptions());
        var surfaceInfo = GetSurfaceInfo(_surface, _adapter);
        _surfaceFormat = surfaceInfo.Format;
        _alphaMode = surfaceInfo.AlphaMode;
        _presentMode = surfaceInfo.PresentMode;
        _device = Wgpu.RequestDevice(_adapter);
        _queue = Wgpu.GetQueue(_device);
    }

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

    private static WgpuHandle<WGPUSurface> CreateSurface(
        WgpuHandle<WGPUInstance> instance,
        GlfwWindow window)
    {
#if BROWSER
        return Wgpu.CreateCanvasSurface(instance, "#canvas", "Sia.UI showcase surface");
#else
        if (OperatingSystem.IsWindows()) {
            return Wgpu.CreateWindowsSurface(
                instance,
                GlfwPlatformNative.GetCurrentWin32ModuleHandle(),
                Glfw.GetWin32Window(window),
                "Sia.UI showcase surface");
        }
        if (OperatingSystem.IsLinux()) {
            var waylandDisplay = Glfw.GetWaylandDisplay();
            if (waylandDisplay != 0) {
                return Wgpu.CreateWaylandSurface(
                    instance,
                    waylandDisplay,
                    Glfw.GetWaylandWindow(window),
                    "Sia.UI showcase surface");
            }
            return Wgpu.CreateXlibSurface(
                instance,
                Glfw.GetX11Display(),
                (ulong)Glfw.GetX11Window(window),
                "Sia.UI showcase surface");
        }
        throw new PlatformNotSupportedException(
            "This example currently supports Win32, X11 and Wayland surfaces.");
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
            if (capabilities.FormatCount == 0) {
                throw new WgpuException("The surface exposes no texture formats.");
            }
            return new(
                PickSurfaceFormat(in capabilities),
                PickAlphaMode(in capabilities),
                PickPresentMode(in capabilities));
        }
        finally {
            WgpuUnsafe.wgpuSurfaceCapabilitiesFreeMembers(capabilities);
        }
    }

    private static WGPUTextureFormat PickSurfaceFormat(
        scoped in WGPUSurfaceCapabilities capabilities)
    {
        WGPUTextureFormat[] preferred = [
            WGPUTextureFormat.BGRA8Unorm,
            WGPUTextureFormat.RGBA8Unorm,
            WGPUTextureFormat.BGRA8UnormSrgb,
            WGPUTextureFormat.RGBA8UnormSrgb,
        ];
        foreach (var format in preferred) {
            for (nuint index = 0; index < capabilities.FormatCount; index++) {
                if (capabilities.Formats[index] == format) {
                    return format;
                }
            }
        }
        return capabilities.Formats[0];
    }

    private static WGPUCompositeAlphaMode PickAlphaMode(
        scoped in WGPUSurfaceCapabilities capabilities)
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

    private static WGPUPresentMode PickPresentMode(
        scoped in WGPUSurfaceCapabilities capabilities)
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
        ResizeUi(new Size(_framebufferWidth, _framebufferHeight));
        return true;
    }

    private void RenderFrame()
    {
        var frame = Wgpu.AcquireSurfaceTexture(_surface);
        if (frame.Status is not (
            WGPUSurfaceGetCurrentTextureStatus.SuccessOptimal
            or WGPUSurfaceGetCurrentTextureStatus.SuccessSuboptimal)) {
            if (frame.HasTexture) {
                Wgpu.Release(ref frame);
            }
            if (frame.Status is WGPUSurfaceGetCurrentTextureStatus.Outdated
                or WGPUSurfaceGetCurrentTextureStatus.Lost) {
                ResizeIfNeeded(force: true);
                return;
            }
            if (frame.Status == WGPUSurfaceGetCurrentTextureStatus.Timeout) {
                return;
            }
            throw new WgpuException($"Surface acquisition failed with status {frame.Status}.");
        }

        var view = default(WgpuHandle<WGPUTextureView>);
        var commandEncoder = default(WgpuHandle<WGPUCommandEncoder>);
        var renderPass = default(WgpuHandle<WGPURenderPassEncoder>);
        var commandBuffer = default(WgpuHandle<WGPUCommandBuffer>);
        try {
            var viewDescriptor = WGPUTextureViewDescriptor.Default;
            viewDescriptor.Format = _surfaceFormat;
            view = Wgpu.CreateTextureView(frame, in viewDescriptor);
            commandEncoder = Wgpu.CreateCommandEncoder(_device);

            var attachment = WGPURenderPassColorAttachment.Default;
            attachment.View = Pointer(view);
            attachment.LoadOp = WGPULoadOp.Clear;
            attachment.StoreOp = WGPUStoreOp.Store;
            attachment.ClearValue = new WGPUColor {
                R = 0.025,
                G = 0.03,
                B = 0.045,
                A = 1.0,
            };
            var descriptor = WGPURenderPassDescriptor.Default;
            descriptor.ColorAttachmentCount = 1;
            descriptor.ColorAttachments = &attachment;
            renderPass = Wgpu.BeginRenderPass(commandEncoder, in descriptor);
            RenderUi(renderPass, new Size(_framebufferWidth, _framebufferHeight));
            Wgpu.EndRenderPass(renderPass);
            Wgpu.Release(ref renderPass);

            commandBuffer = Wgpu.FinishCommandEncoder(
                commandEncoder,
                WGPUCommandBufferDescriptor.Default);
            Wgpu.Submit(_queue, [commandBuffer]);
            Wgpu.PresentSurfaceOrThrow(_surface);
        }
        finally {
            Wgpu.Release(ref commandBuffer);
            Wgpu.Release(ref renderPass);
            Wgpu.Release(ref commandEncoder);
            Wgpu.Release(ref view);
            Wgpu.Release(ref frame);
        }
    }

    public void Dispose()
    {
        if (_disposed) {
            return;
        }
        _disposed = true;
        DisposeUi();
        Wgpu.Release(ref _queue);
        if (!_surface.IsNull && _surfaceConfigured) {
            Wgpu.UnconfigureSurface(_surface);
        }
        if (!_device.IsNull) {
            Wgpu.DestroyDevice(_device);
        }
        Wgpu.Release(ref _device);
        Wgpu.Release(ref _adapter);
        Wgpu.Release(ref _surface);
        Wgpu.Release(ref _instance);
        _windowWorld?.Dispose();
        _windowWorld = null;
        _windowEntity = null;
        _window = default;
    }

    private static T* Pointer<T>(WgpuHandle<T> handle)
        where T : unmanaged => (T*)handle.DangerousGetHandle();

    private readonly record struct SurfaceInfo(
        WGPUTextureFormat Format,
        WGPUCompositeAlphaMode AlphaMode,
        WGPUPresentMode PresentMode);
}
