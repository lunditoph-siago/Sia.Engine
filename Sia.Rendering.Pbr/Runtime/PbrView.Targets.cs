using System.Runtime.InteropServices;
using Sia;
using Sia.Engine.Camera;
using Sia.Engine.Lighting;
using Sia.Graphics.Reactive;
using Sia.Math;
using Sia.RenderGraph;
using Sia.WebGPU;
using CameraComponent = Sia.Engine.Camera.Camera;

namespace Sia.Engine.Rendering.Pbr;

internal sealed unsafe partial class PbrView
{
    private void Resize(uint width, uint height)
    {
        if (width == _width && height == _height && _hasVisibilityTargets == !UseForward) return;
        _owner.RequireRetirementCapacity(this);
        var used = checked(Bytes + _owner.RetiredBytes(this));
        if (used > _owner.Settings.ViewBytes) throw new InvalidOperationException("Pending view retirements exceed the view byte budget.");
        var next = new GpuResources(_gpuFrame, _owner.Settings.ViewBytes - used);
        try {
            Entity Target(WGPUTextureFormat format, uint bytes, WGPUTextureUsage usage, uint layers = 1)
            {
                var desc = WGPUTextureDescriptor.Default;
                desc.Dimension = WGPUTextureDimension._2D;
                desc.Size = new() {
                    Width = width,
                    Height = height,
                    DepthOrArrayLayers = layers
                };
                desc.Format = format;
                desc.Usage = usage;
                var dimension = WGPUTextureBindingViewDimension.Default; dimension.TextureBindingViewDimension = WGPUTextureViewDimension._2DArray;
                if (layers > 1 && OperatingSystem.IsBrowser()) desc.NextInChain = &dimension.Chain;
                return next.Texture(desc, checked((ulong)width * height * bytes * layers));
            }

            Entity ViewOf(Entity texture, bool array = false) {
                var descriptor = WGPUTextureViewDescriptor.Default;
                if (array) descriptor.Dimension = WGPUTextureViewDimension._2DArray;
                return next.Own(Wgpu.CreateTextureView(texture.GetWgpu<WGPUTexture>(), descriptor));
            }

            Entity Packed(WGPUTextureUsage usage, uint logicalLayers = 1)
                => Target(PbrReflectionStorage.Format, PbrReflectionStorage.BytesPerTexel, usage, PbrReflectionStorage.Layers(logicalLayers));

            var id = UseForward ? default : Target(WGPUTextureFormat.R32Uint, 4, WGPUTextureUsage.RenderAttachment | WGPUTextureUsage.TextureBinding);
            var depth = Target(WGPUTextureFormat.Depth32Float, 4, WGPUTextureUsage.RenderAttachment | WGPUTextureUsage.TextureBinding);
            var hdr = Target(WGPUTextureFormat.RGBA16Float, 8, WGPUTextureUsage.StorageBinding | WGPUTextureUsage.TextureBinding | WGPUTextureUsage.RenderAttachment | WGPUTextureUsage.CopySrc);
            var opaqueHdr = HasReflections ? Packed(WGPUTextureUsage.StorageBinding | WGPUTextureUsage.TextureBinding) : hdr;
            var reflectionSpecular = HasReflections ? Packed(
                WGPUTextureUsage.StorageBinding | WGPUTextureUsage.TextureBinding | WGPUTextureUsage.CopySrc, 2) : default;
            var reflectionSignal = HasReflections ? Packed(
                WGPUTextureUsage.StorageBinding | WGPUTextureUsage.TextureBinding | WGPUTextureUsage.CopySrc) : default;
            var reflectionMetadata = HasReflections ? Packed(
                WGPUTextureUsage.StorageBinding | WGPUTextureUsage.TextureBinding | WGPUTextureUsage.CopySrc, 3) : default;
            const WGPUTextureUsage historyUsage = WGPUTextureUsage.StorageBinding | WGPUTextureUsage.TextureBinding;
            var filteredSignal = HasHistory ? Packed(historyUsage) : default;
            var previousSignal = HasHistory ? Packed(historyUsage) : default;
            var previousReceiver = HasHistory ? Packed(historyUsage) : default;
            var previousNormal = HasHistory ? Target(WGPUTextureFormat.RGBA16Float, 8, historyUsage) : default;
            var previousMetadata = HasHistory ? Packed(historyUsage, 3) : default;
            var previousHit = HasHistory ? Packed(historyUsage) : default;
            Entity snapshot;
            if (_owner.Materials.HasTransmission)
                snapshot = Target(WGPUTextureFormat.RGBA16Float, 8, WGPUTextureUsage.CopyDst | WGPUTextureUsage.TextureBinding);
            else {
                var desc = WGPUTextureDescriptor.Default;
                desc.Dimension = WGPUTextureDimension._2D;
                desc.Size = new() {
                    Width = 1,
                    Height = 1,
                    DepthOrArrayLayers = 1
                };
                desc.Format = WGPUTextureFormat.RGBA16Float;
                desc.Usage = WGPUTextureUsage.TextureBinding;
                snapshot = next.Texture(desc, 8);
            }
            var stride = checked(((width + 7) / 8 * ((height + 7) / 8)) + 4);
            var tiles = UseForward ? default : next.Buffer(checked((ulong)stride * (ulong)_owner.Materials.Groups.Length * 4), WGPUBufferUsage.Storage | WGPUBufferUsage.Indirect);
            var idView = UseForward ? default : ViewOf(id);
            var depthView = ViewOf(depth);
            var hdrView = ViewOf(hdr);
            var snapshotView = ViewOf(snapshot);
            var normalRoughness = _owner.Settings.ExportSurfaceData
                ? Target(WGPUTextureFormat.RGBA16Float, 8, WGPUTextureUsage.StorageBinding | WGPUTextureUsage.TextureBinding | WGPUTextureUsage.CopySrc)
                : default;
            var baseMetallic = _owner.Settings.ExportSurfaceData
                ? Target(WGPUTextureFormat.RGBA8Unorm, 4, WGPUTextureUsage.StorageBinding | WGPUTextureUsage.TextureBinding | WGPUTextureUsage.CopySrc)
                : default;
            Entity resolve = default;
            if (!UseForward) {
                var entries = new List<WGPUBindGroupEntry> {
                    GpuBinding.Texture(0, idView.GetWgpu<WGPUTextureView>()),
                    GpuBinding.Texture(1, depthView.GetWgpu<WGPUTextureView>()),
                    GpuBinding.Buffer(2, tiles),
                    GpuBinding.Texture(3, ViewOf(opaqueHdr, array: HasReflections).GetWgpu<WGPUTextureView>())
                };
                if (_owner.Settings.ExportSurfaceData) {
                    entries.Add(GpuBinding.Texture(4, ViewOf(normalRoughness).GetWgpu<WGPUTextureView>()));
                    entries.Add(GpuBinding.Texture(5, ViewOf(baseMetallic).GetWgpu<WGPUTextureView>()));
                }
                if (_selection is not null) entries.Add(GpuBinding.Buffer(6, _selection.Work));
                if (HasReflections) entries.Add(GpuBinding.Texture(7, ViewOf(reflectionSpecular, array: true).GetWgpu<WGPUTextureView>()));
                resolve = GpuBinding.Group(next, _owner.Pipelines.ResolveLayout,
                    CollectionsMarshal.AsSpan(entries));
            }
            Entity tile = default;
            if (!UseForward) {
                var entries = new List<WGPUBindGroupEntry> {
                    GpuBinding.Texture(0, idView.GetWgpu<WGPUTextureView>()), GpuBinding.Buffer(2, tiles)
                };
                if (_selection is not null) entries.Add(GpuBinding.Buffer(6, _selection.Work));
                tile = GpuBinding.Group(next, _owner.Pipelines.TileLayout, CollectionsMarshal.AsSpan(entries));
            }
            var background = !UseForward ? default : GpuBinding.Group(next, _owner.Pipelines.BackgroundLayout, [
                GpuBinding.Buffer(0, _uniform),
                GpuBinding.Texture(1, _owner.Environment.CubeView.GetWgpu<WGPUTextureView>()),
                GpuBinding.Sampler(2, _owner.Environment.Sampler),
                GpuBinding.Texture(3, hdrView.GetWgpu<WGPUTextureView>()),
                GpuBinding.Texture(4, depthView.GetWgpu<WGPUTextureView>()),
                GpuBinding.Sampler(5, _depthSampler)
            ]);
            var glass = GpuBinding.Group(next, _owner.Pipelines.GlassLayout, [
                GpuBinding.Texture(0, snapshotView.GetWgpu<WGPUTextureView>()),
                GpuBinding.Texture(1, depthView.GetWgpu<WGPUTextureView>())
            ]);
            var output = GpuBinding.Group(next, _owner.Pipelines.OutputLayout, [
                GpuBinding.Buffer(0, _outputUniform),
                GpuBinding.Texture(1, hdrView.GetWgpu<WGPUTextureView>()),
                GpuBinding.Sampler(2, _sampler)
            ]);
            Entity traceGroup = default, composeGroup = default;
            Entity historyGroup = default, historyCommitGroup = default, historyCommitHitGroup = default;
            if (HasReflections) {
                var probes = _owner.Probes!;
                var specularView = ViewOf(reflectionSpecular, array: true); var signalView = ViewOf(reflectionSignal, array: true);
                var metadataView = ViewOf(reflectionMetadata, array: true); var normalView = ViewOf(normalRoughness);
                traceGroup = GpuBinding.Group(next, _owner.Pipelines.ReflectionTraceLayout, [
                    GpuBinding.Buffer(0, _uniform), GpuBinding.Buffer(1, _lightData), GpuBinding.Buffer(2, probes.Tracing),
                    GpuBinding.Buffer(3, probes.TracingGeometry), GpuBinding.Buffer(4, probes.TracingInstances), GpuBinding.Buffer(5, _reflectionConfig),
                    GpuBinding.Texture(6, depthView.GetWgpu<WGPUTextureView>()), GpuBinding.Texture(7, ViewOf(normalRoughness).GetWgpu<WGPUTextureView>()),
                    GpuBinding.Texture(8, specularView.GetWgpu<WGPUTextureView>()), GpuBinding.Texture(9, signalView.GetWgpu<WGPUTextureView>()),
                    GpuBinding.Texture(10, probes.TextureView.GetWgpu<WGPUTextureView>()), GpuBinding.Sampler(11, _owner.Environment.Sampler),
                    GpuBinding.Buffer(12, probes.Header), GpuBinding.Buffer(13, _owner.Environment.Sh),
                    GpuBinding.Texture(14, metadataView.GetWgpu<WGPUTextureView>()),
                    GpuBinding.Buffer(15, _reflectionPoses), GpuBinding.Buffer(16, _reflectionReceiverMap)
                ]);
                var compositionView = signalView;
                if (HasHistory) {
                    compositionView = ViewOf(filteredSignal, array: true);
                    var previousSignalView = ViewOf(previousSignal, array: true); var previousReceiverView = ViewOf(previousReceiver, array: true);
                    var previousNormalView = ViewOf(previousNormal); var previousMetadataView = ViewOf(previousMetadata, array: true);
                    var previousHitView = ViewOf(previousHit, array: true);
                    historyGroup = GpuBinding.Group(next, _owner.Pipelines.ReflectionHistoryLayout, [
                        GpuBinding.Buffer(0, _uniform), GpuBinding.Buffer(1, _historyConfig), GpuBinding.Buffer(2, _reflectionPoses), GpuBinding.Buffer(3, _reflectionReceiverMap),
                        GpuBinding.Texture(4, signalView.GetWgpu<WGPUTextureView>()), GpuBinding.Texture(5, metadataView.GetWgpu<WGPUTextureView>()),
                        GpuBinding.Texture(6, specularView.GetWgpu<WGPUTextureView>()), GpuBinding.Texture(7, normalView.GetWgpu<WGPUTextureView>()),
                        GpuBinding.Texture(8, depthView.GetWgpu<WGPUTextureView>()), GpuBinding.Texture(9, previousSignalView.GetWgpu<WGPUTextureView>()),
                        GpuBinding.Texture(10, previousReceiverView.GetWgpu<WGPUTextureView>()), GpuBinding.Texture(11, previousNormalView.GetWgpu<WGPUTextureView>()),
                        GpuBinding.Texture(12, previousMetadataView.GetWgpu<WGPUTextureView>()), GpuBinding.Texture(13, previousHitView.GetWgpu<WGPUTextureView>()),
                        GpuBinding.Texture(14, compositionView.GetWgpu<WGPUTextureView>())
                    ]);
                    var commitEntries = new List<WGPUBindGroupEntry> {
                        GpuBinding.Buffer(0, _uniform), GpuBinding.Buffer(1, _historyConfig),
                        GpuBinding.Texture(2, compositionView.GetWgpu<WGPUTextureView>()), GpuBinding.Texture(3, metadataView.GetWgpu<WGPUTextureView>()),
                        GpuBinding.Texture(4, specularView.GetWgpu<WGPUTextureView>()), GpuBinding.Texture(5, normalView.GetWgpu<WGPUTextureView>()),
                        GpuBinding.Texture(6, depthView.GetWgpu<WGPUTextureView>())
                    };
                    var hitEntries = new List<WGPUBindGroupEntry>(commitEntries) { GpuBinding.Texture(7, previousHitView.GetWgpu<WGPUTextureView>()) };
                    historyCommitHitGroup = GpuBinding.Group(next, _owner.Pipelines.ReflectionCommitHitLayout, CollectionsMarshal.AsSpan(hitEntries));
                    commitEntries.Add(GpuBinding.Texture(7, previousSignalView.GetWgpu<WGPUTextureView>()));
                    commitEntries.Add(GpuBinding.Texture(8, previousReceiverView.GetWgpu<WGPUTextureView>()));
                    commitEntries.Add(GpuBinding.Texture(9, previousNormalView.GetWgpu<WGPUTextureView>()));
                    commitEntries.Add(GpuBinding.Texture(10, previousMetadataView.GetWgpu<WGPUTextureView>()));
                    historyCommitGroup = GpuBinding.Group(next, _owner.Pipelines.ReflectionCommitLayout, CollectionsMarshal.AsSpan(commitEntries));
                }
                composeGroup = GpuBinding.Group(next, _owner.Pipelines.ReflectionComposeLayout, [
                    GpuBinding.Texture(0, ViewOf(opaqueHdr, array: true).GetWgpu<WGPUTextureView>()), GpuBinding.Texture(1, specularView.GetWgpu<WGPUTextureView>()),
                    GpuBinding.Texture(2, compositionView.GetWgpu<WGPUTextureView>()), GpuBinding.Texture(3, hdrView.GetWgpu<WGPUTextureView>()),
                    GpuBinding.Buffer(4, _outputUniform)
                ]);
            }
            _owner.RetireTargets(this, _sizeResources);
            _sizeResources = next;
            _width = width;
            _height = height;
            _id = id;
            _normalRoughness = normalRoughness;
            _baseMetallic = baseMetallic;
            _depth = depth;
            _hdr = hdr;
            _opaqueHdr = opaqueHdr; _reflectionSpecular = reflectionSpecular; _reflectionSignal = reflectionSignal; _reflectionMetadata = reflectionMetadata;
            _reflectionTraceGroup = traceGroup; _reflectionComposeGroup = composeGroup;
            _filteredSignal = filteredSignal; _previousSignal = previousSignal; _previousReceiver = previousReceiver;
            _previousNormal = previousNormal; _previousMetadata = previousMetadata; _previousHit = previousHit;
            _historyGroup = historyGroup; _historyCommitGroup = historyCommitGroup; _historyCommitHitGroup = historyCommitHitGroup;
            InvalidateReflectionHistory();
            _snapshot = snapshot;
            _tiles = tiles;
            _resolveGroup = resolve;
            _tileGroup = tile;
            _glassGroup = glass;
            _outputGroup = output;
            _tileStride = stride;
            _backgroundGroup = background;
            _hasVisibilityTargets = !UseForward;
        }
        catch {
            next.Dispose();
            throw;
        }
    }
}
