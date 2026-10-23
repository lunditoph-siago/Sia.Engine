using Sia;
using Sia.Graphics.Reactive;
using Sia.RenderGraph;
using Sia.WebGPU;
using Sia.Math;

namespace Sia.Engine.Rendering.Pbr;

internal sealed unsafe partial class PbrView
{
    private Entity _opaqueHdr, _reflectionSpecular, _reflectionSignal, _reflectionMetadata;
    private Entity _reflectionTraceGroup, _reflectionComposeGroup;
    private readonly Entity _reflectionConfig;
    private readonly RenderGraphTextureKey _opaqueHdrKey, _reflectionSpecularKey, _reflectionSignalKey, _reflectionMetadataKey;
    private readonly RenderGraphBufferKey _reflectionConfigKey;
    private Entity _reflectionPoses, _reflectionReceiverMap;
    private readonly RenderGraphBufferKey _reflectionPosesKey, _reflectionReceiverMapKey;
    private PbrReflectionInstancePose[]? _reflectionCurrentPoses;
    private uint4[]? _reflectionPoseRows;
    private PbrBvhTrace? _poseTrace;
    private bool HasReflections => _owner.Settings.Reflections is not null;
    private RenderGraphTextureKey OpaqueTargetKey => HasReflections ? _opaqueHdrKey : _hdrKey;

    private void TraceReflections(WgpuReactiveRenderGraphPassContext context)
        => DispatchReflection(context, _owner.Pipelines.ReflectionTrace, _reflectionTraceGroup);

    private void ComposeReflections(WgpuReactiveRenderGraphPassContext context)
        => DispatchReflection(context, _owner.Pipelines.ReflectionCompose, _reflectionComposeGroup);

    private void DispatchReflection(WgpuReactiveRenderGraphPassContext context, Entity pipeline, Entity group)
    {
        var pass = Wgpu.BeginComputePass(context.CommandEncoder, WGPUComputePassDescriptor.Default);
        try {
            Wgpu.SetComputePipeline(pass, pipeline.GetWgpu<WGPUComputePipeline>());
            Wgpu.SetBindGroup(pass, 0, group.GetWgpu<WGPUBindGroup>());
            Wgpu.DispatchWorkgroups(pass, (_width + 7) / 8, (_height + 7) / 8);
        }
        finally { Wgpu.EndComputePass(pass); Wgpu.Release(ref pass); }
    }

    private void BuildReflections(ref RenderGraphBuildContext graph, Dependencies dependency)
    {
        if (HasReflections) {
            graph.UsePass(new(_prefix + "reflection-trace"), "pbr-reflection-trace", dependency, static (in d, p) =>
                d.View.ReadProbeTrace(p).Read(d.View._frameKey, RenderGraphBufferUsage.Uniform).Read(d.View._lightsKey, RenderGraphBufferUsage.Storage)
                    .Read(d.View._reflectionConfigKey, RenderGraphBufferUsage.Uniform)
                    .Read(d.View._environmentShKey, RenderGraphBufferUsage.Uniform)
                    .Read(d.View._reflectionPosesKey, RenderGraphBufferUsage.Storage).Read(d.View._reflectionReceiverMapKey, RenderGraphBufferUsage.Storage)
                    .Read(d.View._probeHeaderKey, RenderGraphBufferUsage.Uniform).Read(d.View._probeTextureKey, RenderGraphTextureUsage.TextureBinding)
                    .Read(d.Depth, RenderGraphTextureUsage.TextureBinding).Read(d.View._normalRoughnessKey, RenderGraphTextureUsage.TextureBinding)
                    .Read(d.View._reflectionSpecularKey, RenderGraphTextureUsage.TextureBinding)
                    .Write(d.View._reflectionSignalKey, RenderGraphTextureUsage.StorageBinding)
                    .Write(d.View._reflectionMetadataKey, RenderGraphTextureUsage.StorageBinding), TraceReflections, RenderGraphPassKind.Compute);
        }
        TimingMarker(ref graph, dependency, 8);
        BuildHistoryFilter(ref graph, dependency);
        if (HasReflections)
            graph.UsePass(new(_prefix + "reflection-compose"), "pbr-reflection-compose", dependency, static (in d, p) =>
                p.Read(d.View._opaqueHdrKey, RenderGraphTextureUsage.TextureBinding)
                    .Read(d.View._frameKey, RenderGraphBufferUsage.Uniform)
                    .Read(d.View._reflectionSpecularKey, RenderGraphTextureUsage.TextureBinding)
                    .Read(d.View.CompositionSignalKey, RenderGraphTextureUsage.TextureBinding)
                    .Write(d.View._hdrKey, RenderGraphTextureUsage.StorageBinding), ComposeReflections, RenderGraphPassKind.Compute);
        TimingMarker(ref graph, dependency, 9);
        BuildHistoryCommit(ref graph, dependency);
    }

    private void ImportReflectionTargets(ref RenderGraphBuildContext graph)
    {
        if (!HasReflections) return;
        Import(ref graph, _opaqueHdrKey, _opaqueHdr); Import(ref graph, _reflectionSpecularKey, _reflectionSpecular);
        Import(ref graph, _reflectionSignalKey, _reflectionSignal); Import(ref graph, _reflectionMetadataKey, _reflectionMetadata);
        Import(ref graph, _reflectionConfigKey, _reflectionConfig, RenderGraphBufferUsage.Uniform);
        Import(ref graph, _reflectionPosesKey, _reflectionPoses, RenderGraphBufferUsage.Storage);
        Import(ref graph, _reflectionReceiverMapKey, _reflectionReceiverMap, RenderGraphBufferUsage.Storage);
        ImportHistory(ref graph);
    }

    private void PrepareReflectionPoses()
    {
        if (!HasReflections || ReferenceEquals(_poseTrace, _owner.ReflectionTrace)) return;
        _owner.ReflectionTrace.CopyPoses(_reflectionCurrentPoses!);
        // Current identity export does not consume previous transforms; temporal use will supply per-view committed poses.
        PbrReflectionPoseData.Pack(_reflectionCurrentPoses!, _reflectionCurrentPoses!, _reflectionPoseRows!);
        Wgpu.WriteBuffer<uint4>(_gpu.Queue, _reflectionPoses.GetWgpu<WGPUBuffer>(), 0, _reflectionPoseRows!);
        _poseTrace = _owner.ReflectionTrace;
    }
}
