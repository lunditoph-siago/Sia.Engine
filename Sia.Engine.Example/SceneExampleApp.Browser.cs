using System.Runtime.InteropServices.JavaScript;
using Sia.GLFW;
using Sia.Input;
using Sia.WebGPU;
using Sia.Window;

namespace Sia.Engine.Example;

#if BROWSER
internal sealed partial class SceneExampleApp
{
    private double? _previousAnimationFrameTime;

    public async Task RunAsync()
    {
        await InitializeAsync();
        Console.WriteLine($"Sia.Engine browser {_pipeline} example - Esc to close.");
        await RunAnimationFrameLoopAsync();
    }

    private bool RenderAnimationFrame(double timestampMilliseconds)
    {
        if (Glfw.ShouldClose(_window)) return false;
        ResizeWindowToCanvas();
        Glfw.PollEvents();
        var currentTime = timestampMilliseconds / 1000.0;
        var deltaTime = _previousAnimationFrameTime is double previous
            ? (float)System.Math.Min(currentTime - previous, 0.1)
            : 0f;
        _previousAnimationFrameTime = currentTime;
        if (Glfw.GetKey(_window, Key.Escape) != InputAction.Release) Glfw.RequestClose(_window);
        if (ResizeIfNeeded()) {
            UpdateScene(deltaTime);
            RenderFrame();
            Wgpu.ProcessEvents(_instance);
        }
        return !Glfw.ShouldClose(_window);
    }

    private async Task InitializeAsync()
    {
        Glfw.Initialize();
        _glfwInitialized = true;
        var initialSize = GetCanvasSize();
        _window = Glfw.CreateWindow(
            new WindowDescriptor(
                initialSize.Width,
                initialSize.Height,
                $"Sia.Engine - {_pipeline} Example",
                Resizable: true),
            new GlfwWindowOptions(ClientApi.NoApi));
        _instance = Wgpu.CreateInstance();
        _surface = CreateSurface(_instance, _window);
        _adapter = await RequestBrowserAdapterAsync();
        var surfaceInfo = GetSurfaceInfo(_surface, _adapter);
        _surfaceFormat = surfaceInfo.Format;
        _alphaMode = surfaceInfo.AlphaMode;
        _presentMode = surfaceInfo.PresentMode;
        _device = await RequestBrowserDeviceAsync();
        _queue = Wgpu.GetQueue(_device);
        InitializeRenderGraph();
        InitializeScene();
        ResizeIfNeeded(force: true);
        UpdateScene(0f);
        RenderFrame();
    }

    private void ResizeWindowToCanvas()
    {
        var target = GetCanvasSize();
        var current = Glfw.GetSize(_window);
        if (target.Width != current.Width || target.Height != current.Height) {
            Glfw.SetSize(_window, target);
        }
    }

    private static WindowSize GetCanvasSize() =>
        new(GetCanvasWidth(), GetCanvasHeight());

    [JSImport("getCanvasWidth", "main.js")]
    private static partial int GetCanvasWidth();

    [JSImport("getCanvasHeight", "main.js")]
    private static partial int GetCanvasHeight();

    [JSImport("setInspectionStatus", "main.js")]
    private static partial void SetInspectionStatus(string status, double distance, bool touring, bool triangles);

    [JSImport("takeInspectionCommands", "main.js")]
    private static partial int TakeInspectionCommands();

    [JSImport("takeInspectionDistance", "main.js")]
    private static partial double TakeInspectionDistance();

    private async Task<WgpuHandle<WGPUAdapter>> RequestBrowserAdapterAsync()
    {
        if (GetBrowserFeatureLevel() == "compatibility") {
            return await Wgpu.RequestAdapterAsync(_instance,
                BuildAdapterOptions(WGPUFeatureLevel.Compatibility, WGPUPowerPreference.Undefined));
        }
        try {
            return await Wgpu.RequestAdapterAsync(_instance, BuildAdapterOptions());
        }
        catch (WgpuException coreError) {
            try {
                return await Wgpu.RequestAdapterAsync(
                    _instance,
                    BuildAdapterOptions(
                        WGPUFeatureLevel.Compatibility,
                        WGPUPowerPreference.Undefined));
            }
            catch (WgpuException compatibilityError) {
                throw new WgpuException(
                    $"Browser adapter requests failed. Core: {coreError.Message} " +
                    $"Compatibility: {compatibilityError.Message}");
            }
        }
    }

    [JSImport("getBrowserFeatureLevel", "main.js")]
    private static partial string GetBrowserFeatureLevel();

    private unsafe Task<WgpuHandle<WGPUDevice>> RequestBrowserDeviceAsync()
    {
        var supportedStages = WGPUCompatibilityModeLimits.Default;
        var supported = WGPULimits.Default;
        supported.NextInChain = &supportedStages.Chain;
        if (WgpuUnsafe.wgpuAdapterGetLimits((WGPUAdapter*)_adapter.DangerousGetHandle(), &supported) != WGPUStatus.Success) {
            throw new WgpuException("The browser adapter did not report its device limits.");
        }
        var vertexStorage = _pipeline == ScenePipeline.Bunny ? 6u : 1u;
        var fragmentStorage = _pipeline == ScenePipeline.Pbr ? 5u : 4u;
        var workgroupSize = _pipeline == ScenePipeline.Bunny ? 256u : 128u;
        if (supportedStages.MaxStorageBuffersInVertexStage == uint.MaxValue
            || supportedStages.MaxStorageBuffersInVertexStage < vertexStorage
            || supportedStages.MaxStorageBuffersInFragmentStage == uint.MaxValue
            || supportedStages.MaxStorageBuffersInFragmentStage < fragmentStorage
            || supported.MaxStorageBuffersPerShaderStage < System.Math.Max(vertexStorage, fragmentStorage)
            || supported.MaxComputeWorkgroupSizeX < workgroupSize
            || supported.MaxComputeInvocationsPerWorkgroup < workgroupSize) {
            throw new WgpuException($"{_pipeline} requires {vertexStorage} vertex storage buffers, " +
                $"{fragmentStorage} fragment storage buffers and {workgroupSize} compute invocations per workgroup. " +
                "The adapter cannot support this rendering path.");
        }
        var requiredStages = WGPUCompatibilityModeLimits.Default;
        requiredStages.MaxStorageBuffersInVertexStage = vertexStorage;
        requiredStages.MaxStorageBuffersInFragmentStage = fragmentStorage;
        var required = WGPULimits.Default;
        required.NextInChain = &requiredStages.Chain;
        required.MaxComputeWorkgroupSizeX = workgroupSize;
        required.MaxComputeInvocationsPerWorkgroup = workgroupSize;
        var descriptor = WGPUDeviceDescriptor.Default;
        descriptor.RequiredLimits = &required;
        return Wgpu.RequestDeviceAsync(_adapter, descriptor);
    }
}
#endif
