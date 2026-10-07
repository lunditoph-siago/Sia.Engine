using System.Runtime.InteropServices;
using Sia;
using Sia.Graphics.Reactive;
using Sia.Math;
using Sia.WebGPU;

namespace Sia.Engine.Rendering.Pbr;

internal sealed unsafe partial class PbrView
{
    [StructLayout(LayoutKind.Sequential)]
    private struct ReflectionHistoryFrame
    {
        public float4x4 PreviousViewProjection;
        public float4 PreviousEye;
        public float4 Parameters;
        public uint4 State;
    }

    private Entity _reflectionHistoryUniform, _reflectionPreviousInstances;
    private Entity _reflectionFiltered, _reflectionHistory, _reflectionPreviousWorld, _reflectionPreviousNormal, _reflectionTemporalGroup;
    private PbrInstanceGpu[] _reflectionInstanceSnapshot = [];
    private float4x4 _reflectionPreviousVp, _reflectionPreviousProjection, _reflectionCurrentProjection;
    private float4 _reflectionPreviousEye;
    private Entity _reflectionPreviousCamera;
    private ulong _reflectionPreviousFrame, _reflectionCurrentFrame;
    private bool _reflectionHistoryReady;

    private RenderGraphTextureKey ReflectionFilteredKey => new(_prefix + "reflection-filtered");
    private RenderGraphTextureKey ReflectionHistoryKey => new(_prefix + "reflection-history");
    private RenderGraphTextureKey ReflectionPreviousWorldKey => new(_prefix + "reflection-previous-world");
    private RenderGraphTextureKey ReflectionPreviousNormalKey => new(_prefix + "reflection-previous-normal");
    private RenderGraphBufferKey ReflectionHistoryFrameKey => new(_prefix + "reflection-history-frame");
    private RenderGraphBufferKey ReflectionPreviousInstancesKey => new(_prefix + "reflection-previous-instances");

    private void InitializeReflectionHistory()
    {
        if (!_owner.Settings.TemporalReflections) return;
        _reflectionInstanceSnapshot = _owner.Scene.InstanceData.ToArray();
        _reflectionHistoryUniform = _gpu.Buffer(112, WGPUBufferUsage.Uniform | WGPUBufferUsage.CopyDst);
        _reflectionPreviousInstances = _gpu.Buffer(checked((ulong)_reflectionInstanceSnapshot.Length * 144),
            WGPUBufferUsage.Storage | WGPUBufferUsage.CopyDst);
    }

    private void PrepareReflectionHistory(float4x4 projection, ulong frame)
    {
        if (!_owner.Settings.TemporalReflections) return;
        var valid = _reflectionHistoryReady && !_frame.CameraCut && _reflectionPreviousCamera == _frame.Camera
            && _reflectionPreviousFrame + 1 == frame && _reflectionPreviousProjection.Equals(projection)
            && _owner.DebugMode == VisibilityDebugMode.Shaded;
        var header = new ReflectionHistoryFrame {
            PreviousViewProjection = _reflectionPreviousVp, PreviousEye = _reflectionPreviousEye,
            Parameters = new(.8f, 0, 0, 0), State = new(valid ? 1u : 0u, 0, 0, 0)
        };
        Wgpu.WriteBuffer<ReflectionHistoryFrame>(_gpu.Queue, _reflectionHistoryUniform.GetWgpu<WGPUBuffer>(), 0, [header]);
        Wgpu.WriteBuffer<PbrInstanceGpu>(_gpu.Queue, _reflectionPreviousInstances.GetWgpu<WGPUBuffer>(), 0, _reflectionInstanceSnapshot);
        _reflectionCurrentFrame = frame;
        _reflectionCurrentProjection = projection;
    }

    private void FilterReflections(WgpuReactiveRenderGraphPassContext context)
    {
        var pass = context.GetOrBeginRenderPass(new WgpuReactiveRenderGraphColorAttachment(ReflectionFilteredKey, WGPULoadOp.Clear));
        if (_owner.DebugMode != VisibilityDebugMode.Shaded) return;
        Wgpu.SetRenderPipeline(pass, _owner.Pipelines.ReflectionTemporal.GetWgpu<WGPURenderPipeline>());
        Wgpu.SetBindGroup(pass, 0, _reflectionFrameGroup.GetWgpu<WGPUBindGroup>());
        Wgpu.SetBindGroup(pass, 1, _reflectionTemporalGroup.GetWgpu<WGPUBindGroup>());
        Wgpu.Draw(pass, 3);
    }

    private void CommitReflectionHistory(WgpuReactiveRenderGraphPassContext context)
    {
        _reflectionHistoryReady = false;
        if (_owner.DebugMode != VisibilityDebugMode.Shaded) return;
        Copy(_reflectionFiltered, _reflectionHistory);
        Copy(_reflectionInputs, _reflectionPreviousWorld, _width);
        Copy(_normalRoughness, _reflectionPreviousNormal);
        _owner.Scene.InstanceData.CopyTo(_reflectionInstanceSnapshot);
        _reflectionPreviousVp = _data.ViewProjection;
        _reflectionPreviousEye = _data.Eye;
        _reflectionPreviousProjection = _reflectionCurrentProjection;
        _reflectionPreviousCamera = _frame.Camera;
        _reflectionPreviousFrame = _reflectionCurrentFrame;
        _reflectionHistoryReady = true;

        void Copy(Entity input, Entity output, uint x = 0)
        {
            var source = new WGPUTexelCopyTextureInfo { Texture = (WGPUTexture*)input.GetWgpu<WGPUTexture>().DangerousGetHandle(),
                Aspect = WGPUTextureAspect.All, Origin = new() { X = x } };
            var target = new WGPUTexelCopyTextureInfo { Texture = (WGPUTexture*)output.GetWgpu<WGPUTexture>().DangerousGetHandle(), Aspect = WGPUTextureAspect.All };
            var size = new WGPUExtent3D { Width = _width, Height = _height, DepthOrArrayLayers = 1 };
            WgpuUnsafe.wgpuCommandEncoderCopyTextureToTexture((WGPUCommandEncoder*)context.CommandEncoder.DangerousGetHandle(), &source, &target, &size);
        }
    }
}
