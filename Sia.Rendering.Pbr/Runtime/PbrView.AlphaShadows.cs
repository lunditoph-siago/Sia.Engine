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
    private List<PbrGpuScene.Draw>[] _alphaShadowDraws = [];
    private ulong[] _alphaShadowRevisions = [], _alphaShadowPoseRevisions = [];
    private uint[] _alphaShadowCounts = [];
    private uint4[] _alphaShadowMetadataCache = [], _alphaShadowCommandCache = [];
    private Entity _alphaShadowCommands;
    private Entity[] _alphaShadowMetadataBuffers = [], _alphaShadowDrawGroups = [], _alphaShadowBundles = [];
    private ulong[] _alphaShadowBundleMaterialRevisions = [];
    private bool[] _alphaShadowCommandsDirty = [];
    private float[][] _alphaShadowDemands = [];
    private float[] _alphaShadowFrameDemand = [];
    private Entity[] _alphaShadowGateGroups = [], _alphaShadowGateRanges = [];
    private RenderGraphBufferKey[] _alphaShadowRangeKeys = [];
    private uint _alphaShadowStride, _alphaShadowCapacity, _alphaShadowRecordsPerChunk;
    private RenderGraphBufferKey _alphaShadowCommandsKey;
    internal uint AlphaShadowRecordedCommands { get; private set; }
    internal uint AlphaShadowBundleCalls { get; private set; }
    internal ulong AlphaShadowBufferBytes { get; private set; }
    private uint AlphaShadowDrawGroupCount { get; set; }

    private void InitializeAlphaShadowResources()
    {
        if (!_owner.Scene.Transparent.Any(draw => _owner.Materials.ShadowCoverage[draw.Material])) return;
        _alphaShadowCapacity = checked((uint)_owner.Scene.Transparent.Length);
        _alphaShadowStride = System.Math.Max(16u, _gpu.Limits.MinUniformBufferOffsetAlignment);
        var capacity = checked((int)(_alphaShadowCapacity * _shadowLayers));
        var metadataBytes = checked((ulong)capacity * _alphaShadowStride);
        if (metadataBytes > uint.MaxValue)
            throw new NotSupportedException("Alpha shadow metadata exceeds dynamic-uniform offset addressing.");
        var required = checked(metadataBytes + (ulong)capacity * 16
            + (ulong)_shadowLayers * 16);
        if (required > _gpu.Budget - _gpu.Bytes)
            throw new ArgumentException("Alpha shadow metadata/commands exceed the remaining view budget.");
        _alphaShadowDraws = [.. Enumerable.Range(0, (int)_shadowLayers).Select(_ => new List<PbrGpuScene.Draw>())];
        _alphaShadowRevisions = new ulong[_shadowLayers]; _alphaShadowPoseRevisions = new ulong[_shadowLayers];
        _alphaShadowCounts = new uint[_shadowLayers];
        _alphaShadowCommandsDirty = new bool[_shadowLayers]; _alphaShadowBundles = new Entity[_shadowLayers];
        _alphaShadowBundleMaterialRevisions = new ulong[_shadowLayers];
        _alphaShadowFrameDemand = new float[_owner.Materials.ShadowCoverage.Length];
        _alphaShadowDemands = [.. Enumerable.Range(0, (int)_shadowLayers).Select(_ => new float[_alphaShadowFrameDemand.Length])];
        _alphaShadowMetadataCache = new uint4[capacity]; _alphaShadowCommandCache = new uint4[capacity];
        // GpuResources validates the complete size of each uniform allocation;
        // chunk backing storage rather than bypassing ordinary admission rules.
        _alphaShadowRecordsPerChunk = checked((uint)(_gpu.Limits.MaxUniformBufferBindingSize / _alphaShadowStride));
        if (_alphaShadowRecordsPerChunk == 0) throw new NotSupportedException("Alpha shadow dynamic-uniform alignment exceeds its binding limit.");
        var chunks = checked((capacity + (int)_alphaShadowRecordsPerChunk - 1) / (int)_alphaShadowRecordsPerChunk);
        _alphaShadowMetadataBuffers = new Entity[chunks];
        _alphaShadowDrawGroups = new Entity[checked((int)_shadowLayers * chunks)];
        for (var chunk = 0; chunk < chunks; chunk++) {
            var records = System.Math.Min(_alphaShadowRecordsPerChunk, (uint)capacity - (uint)chunk * _alphaShadowRecordsPerChunk);
            var buffer = _gpu.Buffer((ulong)records * _alphaShadowStride, WGPUBufferUsage.Uniform | WGPUBufferUsage.CopyDst);
            _alphaShadowMetadataBuffers[chunk] = buffer;
            _sceneBuffers.Add((new(_prefix + "alpha-shadow-metadata/" + chunk), buffer, RenderGraphBufferUsage.Uniform));
        }
        _alphaShadowCommands = _gpu.Buffer(checked((ulong)capacity * 16),
            WGPUBufferUsage.Storage | WGPUBufferUsage.Indirect | WGPUBufferUsage.CopyDst | WGPUBufferUsage.CopySrc);
        _alphaShadowCommandsKey = new(_prefix + "alpha-shadow-commands");
        _sceneBuffers.Add((_alphaShadowCommandsKey, _alphaShadowCommands,
            RenderGraphBufferUsage.Storage | RenderGraphBufferUsage.Indirect));
        AlphaShadowBufferBytes = metadataBytes + checked((ulong)capacity * 16);
        // Reserve replay ranges before selection budget admission. Nongated paths always replay.
        _alphaShadowGateRanges = new Entity[_shadowLayers];
        _alphaShadowRangeKeys = new RenderGraphBufferKey[_shadowLayers];
        for (uint layer = 0; layer < _shadowLayers; layer++) {
            var range = _gpu.Upload<uint4>([new(layer * _alphaShadowCapacity, 0, 1, 0)],
                WGPUBufferUsage.Uniform | WGPUBufferUsage.Storage | WGPUBufferUsage.CopySrc);
            _alphaShadowGateRanges[layer] = range;
            var key = new RenderGraphBufferKey(_prefix + "alpha-shadow-range/" + layer);
            _alphaShadowRangeKeys[layer] = key;
            AlphaShadowBufferBytes += 16;
            _sceneBuffers.Add((key, range,
                RenderGraphBufferUsage.Uniform | RenderGraphBufferUsage.Storage | RenderGraphBufferUsage.CopySource));
        }
    }

    private void InitializeAlphaShadowSelection()
    {
        if (_alphaShadowDraws.Length == 0 || _selection is not { ShadowCacheEnabled: true }) return;
        _alphaShadowGateGroups = new Entity[_shadowLayers];
        for (uint layer = 0; layer < _shadowLayers; layer++) {
            var range = _alphaShadowGateRanges[layer];
            _alphaShadowGateGroups[layer] = GpuBinding.Group(_gpu, _owner.Pipelines.AlphaShadowGateLayout, [
                GpuBinding.Buffer(0, _selection.Arguments), GpuBinding.Buffer(1, _alphaShadowCommands), GpuBinding.Buffer(2, range)]);
        }
    }

    private bool AlphaShadowRevisionChanged(int layer)
        => _alphaShadowDraws.Length != 0 && PbrShadowCoverage.NeedsRefresh(_alphaShadowPoseRevisions[layer],
            _owner.Scene.InstanceRevision, _alphaShadowRevisions[layer], _owner.Materials.ShadowCoverageRevision);

    private void PrepareAlphaShadowLayer(int layer, float4x4 matrix, bool rebuild)
    {
        if (_alphaShadowDraws.Length == 0) return;
        _alphaShadowRevisions[layer] = _owner.Materials.ShadowCoverageRevision;
        _alphaShadowPoseRevisions[layer] = _owner.Scene.InstanceRevision;
        var list = _alphaShadowDraws[layer];
        if (rebuild) {
            _alphaShadowCommandsDirty[layer] = true;
            list.Clear();
            Array.Clear(_alphaShadowDemands[layer]);
            var culler = new FrustumCuller(Frustum.CreateFromViewProjection(matrix));
            var resolution = _owner.Settings.ShadowResolution;
            var lod = ProjectedGeometryError.PrepareLodProjection(matrix, resolution, resolution);
            foreach (var draw in _owner.Scene.Transparent)
                if (_owner.Materials.ShadowCoverage[draw.Material] && _owner.Scene.IsInstanceEnabled(draw.Instance)
                    && culler.Intersects(draw.Bounds))
                    list.Add(SelectRasterLod(draw, matrix, lod, resolution, resolution, _owner.Settings.ShadowTexelError));
            foreach (var draw in list) {
                var pixels = ProjectedBounds.PixelSize(draw.Bounds, matrix,
                    _owner.Settings.ShadowResolution, _owner.Settings.ShadowResolution);
                _alphaShadowDemands[layer][draw.Material] = MathF.Max(_alphaShadowDemands[layer][draw.Material],
                    float.IsFinite(pixels) ? pixels : _owner.Settings.ShadowResolution);
            }
        }
    }

    private void PrepareAlphaShadowCommands()
    {
        AlphaShadowRecordedCommands = AlphaShadowBundleCalls = 0;
        if (_alphaShadowDraws.Length == 0) return;
        // Reuse cached projection footprints. Clean frames combine at most
        // 31x256 scalar demand entries, not every alpha instance projection.
        Array.Clear(_alphaShadowFrameDemand);
        for (uint layer = 0; layer < _shadowLayers; layer++)
            if (_shadowActive[layer])
                for (var material = 0; material < _alphaShadowFrameDemand.Length; material++)
                    _alphaShadowFrameDemand[material] = MathF.Max(_alphaShadowFrameDemand[material], _alphaShadowDemands[layer][material]);
        for (var material = 0; material < _alphaShadowFrameDemand.Length; material++)
            if (_alphaShadowFrameDemand[material] > 0) _owner.Materials.DemandShadowCoverage(material, _alphaShadowFrameDemand[material]);
        for (uint layer = 0; layer < _shadowLayers; layer++) {
            if (!_shadowActive[layer]) continue;
            if (!_alphaShadowCommandsDirty[layer]) {
                if (_alphaShadowBundles[layer] != default
                    && _alphaShadowBundleMaterialRevisions[layer] != _owner.Materials.Revision)
                    RecordAlphaShadowBundle(layer);
                continue;
            }
            var list = _alphaShadowDraws[layer]; var first = layer * _alphaShadowCapacity;
            if (_alphaShadowCounts[layer] != (uint)list.Count) {
                _alphaShadowCounts[layer] = (uint)list.Count;
                if (_alphaShadowGateRanges.Length != 0)
                    Wgpu.WriteBuffer<uint4>(_gpu.Queue, _alphaShadowGateRanges[layer].GetWgpu<WGPUBuffer>(), 0,
                        [new(first, (uint)list.Count, 1, 0)]);
            }
            for (var index = 0; index < list.Count; index++) {
                var draw = list[index]; var slot = checked((int)first + index);
                var metadata = new uint4(draw.Instance, layer, (uint)draw.Material, draw.SourceInstance);
                // All coverage commands have exactly one instance and zero
                // firstInstance. Source geometry retains its ordinary indices.
                var command = new uint4(checked(draw.Count * 3), 1, draw.FirstIndex, 0);
                if (!_alphaShadowMetadataCache[slot].Equals(metadata)) {
                    var chunk = (uint)slot / _alphaShadowRecordsPerChunk;
                    var offset = (uint)slot % _alphaShadowRecordsPerChunk * _alphaShadowStride;
                    Wgpu.WriteBuffer<uint4>(_gpu.Queue, _alphaShadowMetadataBuffers[chunk].GetWgpu<WGPUBuffer>(), offset, [metadata]);
                    _alphaShadowMetadataCache[slot] = metadata;
                }
                if (!_alphaShadowCommandCache[slot].Equals(command)) {
                    Wgpu.WriteBuffer<uint4>(_gpu.Queue, _alphaShadowCommands.GetWgpu<WGPUBuffer>(), checked((ulong)slot * 16), [command]);
                    _alphaShadowCommandCache[slot] = command;
                }
            }
            _alphaShadowCommandsDirty[layer] = false;
            RecordAlphaShadowBundle(layer);
        }
    }

    private RenderGraphPassDeclarationBuilder WriteAlphaShadowCommands(RenderGraphPassDeclarationBuilder pass)
    {
        if (_alphaShadowDraws.Length == 0) return pass;
        pass.ReadWrite(_alphaShadowCommandsKey, RenderGraphBufferUsage.Storage | RenderGraphBufferUsage.Indirect);
        foreach (var key in _alphaShadowRangeKeys)
            pass.ReadWrite(key,
                RenderGraphBufferUsage.Storage | RenderGraphBufferUsage.Uniform);
        return pass;
    }

    private void GateAlphaShadowCommands(WgpuHandle<WGPUCommandEncoder> encoder, uint layer)
    {
        if (_alphaShadowGateGroups.Length == 0 || _alphaShadowCounts[layer] == 0) return;
        var pass = Wgpu.BeginComputePass(encoder, WGPUComputePassDescriptor.Default);
        try {
            Wgpu.SetComputePipeline(pass, _owner.Pipelines.AlphaShadowGate.GetWgpu<WGPUComputePipeline>());
            Wgpu.SetBindGroup(pass, 0, _alphaShadowGateGroups[layer].GetWgpu<WGPUBindGroup>());
            Wgpu.DispatchWorkgroups(pass, (_alphaShadowCounts[layer] + 63) / 64);
        }
        finally { Wgpu.EndComputePass(pass); Wgpu.Release(ref pass); }
    }

    private void RecordAlphaShadowBundle(uint layer)
    {
        if (_alphaShadowBundles[layer] != default) { _gpu.Release(_alphaShadowBundles[layer]); _alphaShadowBundles[layer] = default; }
        _alphaShadowBundleMaterialRevisions[layer] = _owner.Materials.Revision;
        if (_alphaShadowCounts[layer] == 0) return;
        var descriptor = WGPURenderBundleEncoderDescriptor.Default;
        descriptor.DepthStencilFormat = WGPUTextureFormat.Depth32Float; descriptor.SampleCount = 1;
        var encoder = WgpuUnsafe.wgpuDeviceCreateRenderBundleEncoder((WGPUDevice*)_gpu.Device.DangerousGetHandle(), &descriptor);
        if (encoder == null) throw new WgpuException("WebGPU returned a null alpha shadow bundle encoder.");
        try {
        WgpuUnsafe.wgpuRenderBundleEncoderSetBindGroup(encoder, 0, (WGPUBindGroup*)_rasterGroup.GetWgpu<WGPUBindGroup>().DangerousGetHandle(), 0, null);
        WgpuUnsafe.wgpuRenderBundleEncoderSetBindGroup(encoder, 1, (WGPUBindGroup*)_owner.Scene.Group.GetWgpu<WGPUBindGroup>().DangerousGetHandle(), 0, null);
        Entity previous = default; var previousBatch = -1; var first = layer * _alphaShadowCapacity;
        var draws = _alphaShadowDraws[layer];
        for (var index = 0; index < draws.Count; index++) {
            var draw = draws[index]; var pipeline = draw.DoubleSided ? _owner.Pipelines.AlphaShadowDouble : _owner.Pipelines.AlphaShadow;
            if (pipeline != previous) { WgpuUnsafe.wgpuRenderBundleEncoderSetPipeline(encoder,
                (WGPURenderPipeline*)pipeline.GetWgpu<WGPURenderPipeline>().DangerousGetHandle()); previous = pipeline; }
            var batch = _owner.Materials.MaterialBatches[draw.Material];
            if (batch != previousBatch) {
                WgpuUnsafe.wgpuRenderBundleEncoderSetBindGroup(encoder, 2,
                    (WGPUBindGroup*)_owner.Materials.Groups[batch].GetWgpu<WGPUBindGroup>().DangerousGetHandle(), 0, null); previousBatch = batch;
            }
            var slot = checked(first + (uint)index);
            var chunk = slot / _alphaShadowRecordsPerChunk;
            var offset = slot % _alphaShadowRecordsPerChunk * _alphaShadowStride;
            var groupIndex = checked(layer * (uint)_alphaShadowMetadataBuffers.Length + chunk);
            var group = _alphaShadowDrawGroups[groupIndex];
            if (group == default) {
                var metadata = new WGPUBindGroupEntry { Binding = 0,
                    Buffer = (WGPUBuffer*)_alphaShadowMetadataBuffers[chunk].GetWgpu<WGPUBuffer>().DangerousGetHandle(), Size = 16 };
                var range = new WGPUBindGroupEntry { Binding = 1,
                    Buffer = (WGPUBuffer*)_alphaShadowGateRanges[layer].GetWgpu<WGPUBuffer>().DangerousGetHandle(), Size = 16 };
                group = GpuBinding.Group(_gpu, _owner.Pipelines.AlphaShadowDrawLayout, [metadata, range]);
                _alphaShadowDrawGroups[groupIndex] = group;
                AlphaShadowDrawGroupCount++;
            }
            WgpuUnsafe.wgpuRenderBundleEncoderSetBindGroup(encoder, 3,
                (WGPUBindGroup*)group.GetWgpu<WGPUBindGroup>().DangerousGetHandle(), 1, &offset);
            var command = _alphaShadowCommandCache[checked((int)slot)];
            WgpuUnsafe.wgpuRenderBundleEncoderDraw(encoder, command.x, 1u, command.z, 0u);
            AlphaShadowRecordedCommands++;
        }
        var finish = WGPURenderBundleDescriptor.Default;
        var bundle = WgpuUnsafe.wgpuRenderBundleEncoderFinish(encoder, &finish);
        if (bundle == null) throw new WgpuException("WebGPU returned a null alpha shadow bundle.");
        _alphaShadowBundles[layer] = _gpu.Own(new WgpuHandle<WGPURenderBundle>((nint)bundle));
        }
        finally { WgpuUnsafe.wgpuRenderBundleEncoderRelease(encoder); }
    }

    private void DrawAlphaShadows(WgpuHandle<WGPURenderPassEncoder> pass, uint layer)
    {
        if (_alphaShadowBundles.Length == 0 || _alphaShadowBundles[layer] == default) return;
        var bundle = (WGPURenderBundle*)_alphaShadowBundles[layer].GetWgpu<WGPURenderBundle>().DangerousGetHandle();
        WgpuUnsafe.wgpuRenderPassEncoderExecuteBundles((WGPURenderPassEncoder*)pass.DangerousGetHandle(), 1, &bundle);
        AlphaShadowBundleCalls++;
    }
}
