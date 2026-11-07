#if !BROWSER
using System.Diagnostics;
using Sia;
using Sia.Engine.Rendering;
using Sia.WebGPU;

namespace Sia.Engine.Example;

internal sealed unsafe partial class SceneExampleApp
{
    private WGPUBackendType _presentationBackend;
    private WGPUTextureUsage _presentationSurfaceUsages;
    private GpuResources? _presentationResources;
    private Entity _presentationOutput;
    private uint _presentationWidth, _presentationHeight;
    private WGPUTextureFormat _presentationFormat;
    private Task<bool>? _presentationFailureCompletion;
    private long _presentationFailureStarted;

    // Private counters let a task-only normal-window probe prove this path was
    // actually used. Offscreen submission is distinct from copy and present.
    private ulong _presentationFrameAttempts, _presentationGraphSubmissions;
    private ulong _presentationHistoryNotifications, _presentationCopySubmissions, _presentationPresents;
    private ulong _presentationAcquireFailures, _presentationFailureGateWaits, _presentationResizeDrains;
    private ulong _presentationUncertainSubmissions;
    private ulong _presentationOutputBytes, _presentationPeakOutputBytes;
    private ulong _presentationCombinedViewBytes, _presentationPeakCombinedViewBytes;

    private bool UsePresentationCopy => _sceneRenderer is not null
        && _presentationBackend == WGPUBackendType.D3D12
        && (_presentationSurfaceUsages & WGPUTextureUsage.CopyDst) != 0
        && _surfaceFormat is WGPUTextureFormat.BGRA8Unorm or WGPUTextureFormat.BGRA8UnormSrgb
            or WGPUTextureFormat.RGBA8Unorm or WGPUTextureFormat.RGBA8UnormSrgb;

    private void RenderPresentationCopyFrame()
    {
        if (!CompleteFailedPresentation()) return;
        _presentationFrameAttempts++;
        var featureContext = PrepareRenderGraphFrame();
        EnsurePresentationOutput();
        UpdateRenderGraph(in featureContext, _presentationOutput.GetWgpu<WGPUTexture>());
        var submitted = _presentationGraphSubmissions;
        try { ExecuteRenderGraph(presentationCopy: true); }
        catch {
            // An executor exception may occur after the native queue accepted
            // work. Stop normally and drain; never retry it as an abandoned frame.
            if (submitted == _presentationGraphSubmissions) _presentationUncertainSubmissions++;
            throw;
        }

        var surfaceTexture = Wgpu.AcquireSurfaceTexture(_surface);
        var acquisitionStatus = surfaceTexture.Status;
        try {
            if (acquisitionStatus is not (WGPUSurfaceGetCurrentTextureStatus.SuccessOptimal
                or WGPUSurfaceGetCurrentTextureStatus.SuccessSuboptimal)) {
                _presentationAcquireFailures++;
                if (acquisitionStatus is WGPUSurfaceGetCurrentTextureStatus.Timeout
                    or WGPUSurfaceGetCurrentTextureStatus.Outdated or WGPUSurfaceGetCurrentTextureStatus.Lost) {
                    // The graph is already submitted and notified exactly once.
                    // Bound this failure to one pending graph before preparing another.
                    _presentationFailureCompletion = GpuQueueCompletion.AfterSubmittedWork(_queue);
                    _presentationFailureStarted = Stopwatch.GetTimestamp();
                    if (surfaceTexture.HasTexture) Wgpu.Release(ref surfaceTexture);
                    if (acquisitionStatus is WGPUSurfaceGetCurrentTextureStatus.Outdated
                        or WGPUSurfaceGetCurrentTextureStatus.Lost) ResizeIfNeeded(force: true);
                    return;
                }
                throw new WgpuException($"Surface texture acquisition failed with status {acquisitionStatus}.");
            }
            ValidatePresentationDestination(surfaceTexture.Texture);
            CopyPresentationOutput(surfaceTexture.Texture);
            Wgpu.PresentSurfaceOrThrow(_surface);
            _presentationPresents++;
        }
        finally { Wgpu.Release(ref surfaceTexture); }
    }

    private bool CompleteFailedPresentation()
    {
        if (_presentationFailureCompletion is not { } completion) return true;
        Wgpu.ProcessEvents(_instance);
        if (!completion.IsCompleted) {
            _presentationFailureGateWaits++;
            if (Stopwatch.GetElapsedTime(_presentationFailureStarted).TotalSeconds > 30)
                throw new TimeoutException("Submitted presentation failure did not complete within30seconds.");
            Thread.Sleep(1);
            return false;
        }
        if (!completion.GetAwaiter().GetResult())
            throw new WgpuException("Submitted presentation failure did not complete successfully; resources remain retained.");
        _presentationFailureCompletion = null;
        return true;
    }

    private void EnsurePresentationOutput()
    {
        var width = checked((uint)_framebufferWidth); var height = checked((uint)_framebufferHeight);
        if (width == 0 || height == 0) throw new InvalidOperationException("Presentation requires a positive framebuffer.");
        var bytes = checked((ulong)width * height * 4);
        if (_presentationResources is not null
            && (width != _presentationWidth || height != _presentationHeight || _surfaceFormat != _presentationFormat)) {
            // Resize only: avoid a second live private output and keep its byte
            // accounting honest. Successful steady frames never wait here.
            _presentationResizeDrains++;
            var completion = GpuQueueCompletion.AfterSubmittedWork(_queue);
            var started = Stopwatch.GetTimestamp();
            while (!completion.IsCompleted) {
                Wgpu.ProcessEvents(_instance);
                if (Stopwatch.GetElapsedTime(started).TotalSeconds > 30)
                    throw new TimeoutException("Presentation resize did not drain within30seconds.");
                Thread.Sleep(1);
            }
            if (!completion.GetAwaiter().GetResult())
                throw new WgpuException("Presentation resize queue completion failed; old output remains retained.");
            DisposePresentationOutput();
        }
        var renderer = _sceneRenderer!;
        var viewBytes = renderer.FrameStatistics.ViewBytes;
        if (viewBytes > _qualitySettings.ViewBytes || bytes > _qualitySettings.ViewBytes - viewBytes)
            throw new InvalidOperationException("Combined PBR view and presentation output exceed the unchanged view byte budget.");
        if (_presentationResources is null) {
            var frame = new GpuFrame(_sceneWorld!, _renderWorld!.Entities, _renderDevice, _renderQueue);
            var next = new GpuResources(frame, bytes);
            try {
                var descriptor = WGPUTextureDescriptor.Default;
                descriptor.Dimension = WGPUTextureDimension._2D;
                descriptor.Size = new() { Width = width, Height = height, DepthOrArrayLayers = 1 };
                descriptor.Format = _surfaceFormat; descriptor.SampleCount = 1; descriptor.MipLevelCount = 1;
                descriptor.Usage = WGPUTextureUsage.RenderAttachment | WGPUTextureUsage.CopySrc;
                var output = next.Texture(descriptor, bytes);
                _presentationResources = next; _presentationOutput = output;
                _presentationWidth = width; _presentationHeight = height; _presentationFormat = _surfaceFormat;
                _presentationOutputBytes = next.Bytes;
                _presentationPeakOutputBytes = System.Math.Max(_presentationPeakOutputBytes, next.Bytes);
                Console.WriteLine($"Native presentation-copy output: {width}x{height} {_surfaceFormat}, {next.Bytes} additional Example bytes.");
            }
            catch { next.Dispose(); throw; }
        }
        _presentationCombinedViewBytes = checked(viewBytes + _presentationOutputBytes);
        _presentationPeakCombinedViewBytes = System.Math.Max(_presentationPeakCombinedViewBytes, _presentationCombinedViewBytes);
    }

    private void ValidatePresentationDestination(WgpuHandle<WGPUTexture> destination)
    {
        var actual = Wgpu.GetTextureInfo(destination);
        if (actual.Format != _presentationFormat || actual.Dimension != WGPUTextureDimension._2D
            || actual.Size.Width != _presentationWidth || actual.Size.Height != _presentationHeight
            || actual.Size.DepthOrArrayLayers != 1 || actual.SampleCount != 1 || actual.MipLevelCount != 1
            || (actual.Usage & WGPUTextureUsage.CopyDst) == 0)
            throw new InvalidOperationException("Actual surface texture must exactly match the presentation output format/extent and permit copy destination.");
    }

    private void CopyPresentationOutput(WgpuHandle<WGPUTexture> destination)
    {
        WgpuHandle<WGPUCommandEncoder> encoder = default;
        WgpuHandle<WGPUCommandBuffer> commands = default;
        try {
            encoder = Wgpu.CreateCommandEncoder(_device);
            var source = WGPUTexelCopyTextureInfo.Default;
            source.Texture = Pointer(_presentationOutput.GetWgpu<WGPUTexture>()); source.Aspect = WGPUTextureAspect.All;
            var target = WGPUTexelCopyTextureInfo.Default;
            target.Texture = Pointer(destination); target.Aspect = WGPUTextureAspect.All;
            Wgpu.CopyTextureToTexture(encoder, source, target,
                new() { Width = _presentationWidth, Height = _presentationHeight, DepthOrArrayLayers = 1 });
            commands = Wgpu.FinishCommandEncoder(encoder, WGPUCommandBufferDescriptor.Default);
            Wgpu.Submit(_queue, [commands]);
            _presentationCopySubmissions++;
            ThrowGpuError();
        }
        finally { Wgpu.Release(ref commands); Wgpu.Release(ref encoder); }
    }

    private void DisposePresentationOutput()
    {
        _presentationResources?.Dispose();
        _presentationResources = null; _presentationOutput = default;
        _presentationOutputBytes = 0; _presentationCombinedViewBytes = 0;
        _presentationWidth = 0; _presentationHeight = 0;
        _presentationFailureCompletion = null;
    }
}
#endif
