using System.Runtime.InteropServices.JavaScript;
using Sia.GLFW;
using Sia.Math;
using Sia.WebGPU;
using Sia.Window;

namespace Sia.Engine.Example;

#if BROWSER
internal sealed partial class SceneExampleApp
{
    private double? _previousAnimationFrameTime;
    private bool _cameraFocused;
    private bool _compareLodRequested;
    private WindowSize _browserSize;
    private WindowSize _appliedBrowserSize;
    private int _browserCommands;
    private double _browserDistance;
    private float3 _browserMove;
    private float2 _browserLook;
    private float2 _browserTurn;
    private float _browserSpeed = 4;
    private string _browserFeatureLevel = "core";
    private string? _browserComparePose;
    private (double Distance, bool Touring, bool Triangles, bool Atmosphere)? _browserInspection;
    private Task? _browserPublish;

    public async Task RunAsync()
    {
        _browserFeatureLevel = GetBrowserFeatureLevel();
        if (_materialScene is { } scene) SetSceneAttribution(scene.Attribution);
        await Program.BrowserOwner.RunGraphicsAsync(async () => {
            CaptureBrowserState();
            await InitializeAsync();
            PublishBrowserState();
            return 0;
        });
        Program.SetSceneReady();
        Console.WriteLine($"Sia.Engine browser {_pipeline} example - controls ready.");
        await Program.BrowserOwner.RunFramesAsync(timestamp => {
            CaptureBrowserState();
            var running = RenderAnimationFrame(timestamp);
            PublishBrowserState();
            return running;
        });
        if (_browserPublish is { } pending) { await pending; }
        while (await Program.BrowserOwner.RunGraphicsAsync(() => Task.FromResult(DrainGpuTimingStep())))
            await Task.Delay(1);
    }

    private bool RenderAnimationFrame(double timestampMilliseconds)
    {
        Program.BrowserOwner.VerifyGraphicsAccess();
        ThrowGpuError();
        if (Glfw.ShouldClose(_window)) return false;
        ResizeWindowToCanvas();
        Glfw.PollEvents();
        var currentTime = timestampMilliseconds / 1000.0;
        var deltaTime = _previousAnimationFrameTime is double previous
            ? (float)System.Math.Min(currentTime - previous, 0.1)
            : 0f;
        _previousAnimationFrameTime = currentTime;
        if ((_browserCommands & 512) != 0) Glfw.RequestClose(_window);
        if (ResizeIfNeeded()) {
            UpdateScene(deltaTime);
            RenderFrame();
            Wgpu.ProcessEvents(_instance);
        }
        return !Glfw.ShouldClose(_window);
    }

    private async Task InitializeAsync()
    {
        Program.BrowserOwner.VerifyGraphicsAccess();
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
        _appliedBrowserSize = initialSize;
        _instance = Wgpu.CreateInstance();
        _surface = CreateSurface(_instance, _window);
        _adapter = await RequestBrowserAdapterAsync();
        ReadAdapterDescription();
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
        var current = _appliedBrowserSize;
        if (target.Width != current.Width || target.Height != current.Height) {
            Glfw.SetSize(_window, target);
            _appliedBrowserSize = target;
        }
    }

    private WindowSize GetCanvasSize() => _browserSize;

    private void CaptureBrowserState()
    {
        Program.BrowserOwner.VerifyGraphicsAccess();
        _browserCommands &= 128; // Focus persists; commands are consumed once.
        _browserLook = default;
        while (Program.BrowserInputs.TryDequeue(out var input)) {
            _browserSize = new(input.Width, input.Height);
            _browserMove = input.Move;
            _browserLook += input.Look;
            _browserTurn = input.Turn;
            _browserSpeed = input.Speed;
            var atmosphere = (_browserCommands ^ input.Commands) & 32;
            _browserCommands = ((_browserCommands | input.Commands) & ~(128 | 32)) | (input.Commands & 128) | atmosphere;
            if ((input.Commands & 256) != 0) { _browserDistance = input.Distance; }
        }
    }

    private void PublishBrowserState()
    {
        Program.BrowserOwner.VerifyGraphicsAccess();
        if (_browserPublish is { IsCompleted: false }) { return; }
        if (_browserPublish?.Exception is { } error) { throw new InvalidOperationException("Browser status update failed.", error); }
        if (_browserInspection is null && _browserComparePose is null) { return; }
        var inspection = _browserInspection;
        var pose = _browserComparePose;
        _browserInspection = null;
        _browserComparePose = null;
        // Never await a deputy import inside the UI frame callback. Keep the
        // latest status while an earlier update is still in flight.
        _browserPublish = Program.BrowserOwner.RunImportsAsync(() => {
            if (inspection is { } state) { PublishFrame(state.Distance, (state.Touring ? 1 : 0) | (state.Triangles ? 2 : 0) | (state.Atmosphere ? 4 : 0)); }
            if (pose is not null) { CompareLodAtCamera(pose); }
        });
    }

    [JSImport("setInspectionStatus", "main.js")]
    private static partial void PublishFrame(double distance, int flags);

    [JSImport("setSceneAttribution", "main.js")]
    private static partial void SetSceneAttribution(string attribution);

    [JSImport("compareLodAtCamera", "main.js")]
    private static partial void CompareLodAtCamera(string pose);

    private async Task<WgpuHandle<WGPUAdapter>> RequestBrowserAdapterAsync()
    {
        if (_browserFeatureLevel == "compatibility") {
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
        var vertexStorage = _pipeline != ScenePipeline.Unlit ? 6u : 1u;
        var fragmentStorage = _pipeline == ScenePipeline.Pbr ? 5u : 4u;
        var workgroupSize = _pipeline != ScenePipeline.Unlit ? 256u : 128u;
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
        ConfigureSceneLimits(ref required);
        required.NextInChain = &requiredStages.Chain;
        required.MaxComputeWorkgroupSizeX = workgroupSize;
        required.MaxComputeInvocationsPerWorkgroup = workgroupSize;
        var descriptor = CreateDeviceDescriptor();
        descriptor.RequiredLimits = &required;
        var timingFeature = WGPUFeatureName.TimestampQuery;
        _gpuTimingEnabled = Program.GpuTiming && WgpuUnsafe.wgpuAdapterHasFeature((WGPUAdapter*)_adapter.DangerousGetHandle(), timingFeature) != 0;
        if (Program.TargetFps != 0 && !_gpuTimingEnabled) throw new NotSupportedException("Dynamic resolution requires timestamp-query support.");
        descriptor.RequiredFeatureCount = _gpuTimingEnabled ? 1u : 0u;
        descriptor.RequiredFeatures = _gpuTimingEnabled ? &timingFeature : null;
        return Wgpu.RequestDeviceAsync(_adapter, descriptor);
    }
}
#endif
