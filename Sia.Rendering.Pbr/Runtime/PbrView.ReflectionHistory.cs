using System.Runtime.InteropServices;
using Sia;
using Sia.Engine.Camera;
using Sia.Graphics.Reactive;
using Sia.Math;
using Sia.RenderGraph;
using Sia.WebGPU;

namespace Sia.Engine.Rendering.Pbr;

internal sealed unsafe partial class PbrView
{
    private readonly PbrReflectionHistoryState _historyState = new();
    private Entity _historyConfig, _filteredSignal, _previousSignal, _previousReceiver, _previousNormal, _previousMetadata, _previousHit;
    private Entity _historyGroup, _historyCommitGroup, _historyCommitHitGroup;
    private RenderGraphBufferKey _historyConfigKey;
    private RenderGraphTextureKey _filteredSignalKey, _previousSignalKey, _previousReceiverKey, _previousNormalKey, _previousMetadataKey, _previousHitKey;
    private PbrReflectionInstancePose[]? _previousPoses;
    private uint4[]? _uploadedPoseRows;
    private readonly float4[] _historyUniform = new float4[7];
    private bool _historySurfaceEncoded, _historyHitEncoded, _historyEncoded, _historyPrepared;
    private ulong _historyFrame;
    private Entity _historyCamera;
    private float4x4 _historyProjection;
    private float3 _historyForward;
    private float4x4 _pendingHistoryProjection;
    private float3 _pendingHistoryForward;
    private PbrFrame _historyLighting;
    private float4[]? _historyLights;
    private ProceduralSky? _historySky, _pendingHistorySky;
    private ulong _historyMaterialRevision, _pendingHistoryMaterialRevision;
    private ulong _historyTransportRevision, _pendingHistoryTransportRevision;
    private bool HasHistory => _owner.Settings.Reflections?.History is not null;
    private RenderGraphTextureKey CompositionSignalKey => HasHistory ? _filteredSignalKey : _reflectionSignalKey;

    private void InitializeHistory()
    {
        _historyConfigKey = new(_prefix + "history-config"); _filteredSignalKey = new(_prefix + "reflection-filtered");
        _previousSignalKey = new(_prefix + "history-radiance"); _previousReceiverKey = new(_prefix + "history-receiver");
        _previousNormalKey = new(_prefix + "history-normal"); _previousMetadataKey = new(_prefix + "history-identities"); _previousHitKey = new(_prefix + "history-hit");
        if (!HasHistory) return;
        _previousPoses = new PbrReflectionInstancePose[_reflectionCurrentPoses!.Length];
        for (var at = 0; at < _previousPoses.Length; at++) _previousPoses[at] = _reflectionCurrentPoses[at] with { Enabled = false };
        _uploadedPoseRows = new uint4[_reflectionPoseRows!.Length];
        _historyLights = new float4[ShadowMatrixBase];
        _historyConfig = _gpu.Buffer(112, WGPUBufferUsage.Uniform | WGPUBufferUsage.CopyDst);
    }

    private void PrepareHistory()
    {
        if (!HasHistory) { PrepareReflectionPoses(); return; }
        var settings = _owner.Settings.Reflections!.History!; var camera = _frame.Camera.Get<CameraMatrices>();
        var forward = math.inverse(camera.View).c2.xyz;
        var cameraCut = _frame.Camera != _historyCamera || !_historyProjection.Equals(camera.Proj) || math.dot(forward, _historyForward) < .8f;
        _pendingHistorySky = _frame.Frame.MainWorld.AcquireAddon<EnvironmentLighting>().Sky;
        _pendingHistoryMaterialRevision = _owner.ResourceRevision;
        _pendingHistoryTransportRevision = _owner.ReflectionTransportRevision;
        // Light or transport changes can alter radiance without changing either surface's identity.
        var changed = _historyMaterialRevision != _pendingHistoryMaterialRevision
            || _historyTransportRevision != _pendingHistoryTransportRevision || _historySky != _pendingHistorySky
            || _historyLighting.Counts.x != _data.Counts.x || _historyLighting.Counts.y != _data.Counts.y
            || _historyLighting.Size.w != _data.Size.w
            || !MemoryMarshal.CreateReadOnlySpan(ref _historyLighting.Direction0, 8)
                .SequenceEqual(MemoryMarshal.CreateReadOnlySpan(ref _data.Direction0, 8))
            || !_historyLights.AsSpan(0, (int)_data.Counts.x * 4).SequenceEqual(_sceneData.AsSpan(0, (int)_data.Counts.x * 4));
        _pendingHistoryProjection = camera.Proj; _pendingHistoryForward = forward;
        _historyFrame = _owner.PreparedFrameIndex;
        var reusable = _historyState.Prepare(_historyFrame, _width, _height, _data.ViewProjection, _data.Eye.xyz, settings.CameraCutDistance, cameraCut || changed);
        _historyPrepared = true; _historySurfaceEncoded = _historyHitEncoded = _historyEncoded = false;
        var matrix = _historyState.PreviousViewProjection;
        _historyUniform[0] = matrix.c0; _historyUniform[1] = matrix.c1; _historyUniform[2] = matrix.c2; _historyUniform[3] = matrix.c3;
        _historyUniform[4] = new(_historyState.PreviousEye, 0);
        _historyUniform[5] = new(settings.Weight, settings.NormalCosine, settings.RoughnessTolerance, _owner.Settings.Reflections.RayBias);
        _historyUniform[6] = new(settings.PositionTolerance, settings.HitTolerance, reusable ? 1 : 0, 0);
        Wgpu.WriteBuffer<float4>(_gpu.Queue, _historyConfig.GetWgpu<WGPUBuffer>(), 0, _historyUniform);
        _owner.ReflectionTrace.CopyPoses(_reflectionCurrentPoses!);
        PbrReflectionPoseData.Pack(_reflectionCurrentPoses!, _previousPoses!, _reflectionPoseRows!);
        for (var first = 0; first < _reflectionPoseRows!.Length;) {
            if (_reflectionPoseRows[first].Equals(_uploadedPoseRows![first])) { first++; continue; }
            var end = first + 1;
            while (end < _reflectionPoseRows.Length && !_reflectionPoseRows[end].Equals(_uploadedPoseRows![end])) end++;
            Wgpu.WriteBuffer<uint4>(_gpu.Queue, _reflectionPoses.GetWgpu<WGPUBuffer>(), (ulong)first * 16, _reflectionPoseRows.AsSpan(first, end - first));
            _reflectionPoseRows.AsSpan(first, end - first).CopyTo(_uploadedPoseRows!.AsSpan(first, end - first)); first = end;
        }
    }

    internal void SubmitReflectionHistory(ulong frame)
    {
        if (!HasHistory || !_historyPrepared || frame != _historyFrame || !_historyEncoded) return;
        _historyState.Submitted(frame); _reflectionCurrentPoses!.CopyTo(_previousPoses!, 0);
        _historyCamera = _frame.Camera; _historyProjection = _pendingHistoryProjection;
        _historySky = _pendingHistorySky; _historyMaterialRevision = _pendingHistoryMaterialRevision;
        _historyTransportRevision = _pendingHistoryTransportRevision; _historyLighting = _data;
        _sceneData.AsSpan(0, (int)_data.Counts.x * 4).CopyTo(_historyLights);
        _historyForward = _pendingHistoryForward; _historyPrepared = false;
    }

    internal void InvalidateReflectionHistory() { _historyState.Invalidate(); _historyPrepared = false; }

    private void FilterHistory(WgpuReactiveRenderGraphPassContext context)
        => DispatchReflection(context, _owner.Pipelines.ReflectionHistory, _historyGroup);

    private void CommitHistory(WgpuReactiveRenderGraphPassContext context)
    {
        DispatchReflection(context, _owner.Pipelines.ReflectionCommit, _historyCommitGroup); _historySurfaceEncoded = true; MarkHistoryEncoded();
    }
    private void CommitHistoryHit(WgpuReactiveRenderGraphPassContext context)
    {
        DispatchReflection(context, _owner.Pipelines.ReflectionCommitHit, _historyCommitHitGroup); _historyHitEncoded = true; MarkHistoryEncoded();
    }
    private void MarkHistoryEncoded()
    {
        if (_historySurfaceEncoded && _historyHitEncoded && !_historyEncoded) { _historyState.Encoded(_historyFrame); _historyEncoded = true; }
    }

    private void ImportHistory(ref RenderGraphBuildContext graph)
    {
        if (!HasHistory) return;
        Import(ref graph, _historyConfigKey, _historyConfig, RenderGraphBufferUsage.Uniform);
        Import(ref graph, _filteredSignalKey, _filteredSignal); Import(ref graph, _previousSignalKey, _previousSignal);
        Import(ref graph, _previousReceiverKey, _previousReceiver); Import(ref graph, _previousNormalKey, _previousNormal);
        Import(ref graph, _previousMetadataKey, _previousMetadata); Import(ref graph, _previousHitKey, _previousHit);
    }

    private RenderGraphPassDeclarationBuilder ReadHistoryCurrent(RenderGraphPassDeclarationBuilder p)
        => p.Read(_frameKey, RenderGraphBufferUsage.Uniform).Read(_historyConfigKey, RenderGraphBufferUsage.Uniform)
            .Read(_reflectionMetadataKey, RenderGraphTextureUsage.TextureBinding).Read(_reflectionSpecularKey, RenderGraphTextureUsage.TextureBinding)
            .Read(_normalRoughnessKey, RenderGraphTextureUsage.TextureBinding).Read(_frame.DepthTarget, RenderGraphTextureUsage.TextureBinding);

    private void BuildHistoryFilter(ref RenderGraphBuildContext graph, Dependencies dependency)
    {
        if (!HasHistory) return;
        graph.UsePass(new(_prefix + "reflection-history"), "pbr-reflection-history", dependency, static (in d, p) => d.View.ReadHistoryCurrent(p)
            .Read(d.View._reflectionPosesKey, RenderGraphBufferUsage.Storage).Read(d.View._reflectionReceiverMapKey, RenderGraphBufferUsage.Storage)
            .Read(d.View._reflectionSignalKey, RenderGraphTextureUsage.TextureBinding).Read(d.View._previousSignalKey, RenderGraphTextureUsage.TextureBinding)
            .Read(d.View._previousReceiverKey, RenderGraphTextureUsage.TextureBinding).Read(d.View._previousNormalKey, RenderGraphTextureUsage.TextureBinding)
            .Read(d.View._previousMetadataKey, RenderGraphTextureUsage.TextureBinding).Read(d.View._previousHitKey, RenderGraphTextureUsage.TextureBinding)
            .Write(d.View._filteredSignalKey, RenderGraphTextureUsage.StorageBinding), FilterHistory, RenderGraphPassKind.Compute);
    }

    private void BuildHistoryCommit(ref RenderGraphBuildContext graph, Dependencies dependency)
    {
        if (!HasHistory) return;
        graph.UsePass(new(_prefix + "history-commit"), "pbr-reflection-history-commit", dependency, static (in d, p) => d.View.ReadHistoryCurrent(p)
            .Read(d.View._filteredSignalKey, RenderGraphTextureUsage.TextureBinding)
            .Write(d.View._previousSignalKey, RenderGraphTextureUsage.StorageBinding).Write(d.View._previousReceiverKey, RenderGraphTextureUsage.StorageBinding)
            .Write(d.View._previousNormalKey, RenderGraphTextureUsage.StorageBinding).Write(d.View._previousMetadataKey, RenderGraphTextureUsage.StorageBinding), CommitHistory, RenderGraphPassKind.Compute);
        graph.UsePass(new(_prefix + "history-hit-commit"), "pbr-reflection-history-hit-commit", dependency, static (in d, p) => d.View.ReadHistoryCurrent(p)
            .Read(d.View._filteredSignalKey, RenderGraphTextureUsage.TextureBinding).Write(d.View._previousHitKey, RenderGraphTextureUsage.StorageBinding),
            CommitHistoryHit, RenderGraphPassKind.Compute);
    }
}
