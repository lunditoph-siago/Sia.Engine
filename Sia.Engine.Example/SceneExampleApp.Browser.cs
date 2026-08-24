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
        Console.WriteLine("Sia.Engine browser scene example - Esc to close.");
        await RunAnimationFrameLoopAsync();
    }

    private bool RenderAnimationFrame(double timestampMilliseconds)
    {
        if (Glfw.ShouldClose(_window)) return false;
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
        _window = Glfw.CreateWindow(
            new WindowDescriptor(_initialWidth, _initialHeight, "Sia.Engine - Scene Example", Resizable: true),
            new GlfwWindowOptions(ClientApi.NoApi));
        _instance = Wgpu.CreateInstance();
        _surface = CreateSurface(_instance, _window);
        _adapter = await Wgpu.RequestAdapterAsync(_instance, BuildAdapterOptions());
        var surfaceInfo = GetSurfaceInfo(_surface, _adapter);
        _surfaceFormat = surfaceInfo.Format;
        _alphaMode = surfaceInfo.AlphaMode;
        _presentMode = surfaceInfo.PresentMode;
        _device = await Wgpu.RequestDeviceAsync(_adapter);
        _queue = Wgpu.GetQueue(_device);
        InitializeRenderGraph();
        InitializeScene();
        ResizeIfNeeded(force: true);
        UpdateScene(0f);
        RenderFrame();
    }
}
#endif
