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
        var next = new GpuResources(_gpuFrame, _owner.Settings.ViewBytes - Bytes);
        try {
            Entity Target(WGPUTextureFormat format, uint bytes, WGPUTextureUsage usage, uint pixelsPerColumn = 1)
            {
                var desc = WGPUTextureDescriptor.Default;
                desc.Dimension = WGPUTextureDimension._2D;
                desc.Size = new() {
                    Width = checked(width * pixelsPerColumn),
                    Height = height,
                    DepthOrArrayLayers = 1
                };
                desc.Format = format;
                desc.Usage = usage;
                return next.Texture(desc, checked((ulong)desc.Size.Width * height * bytes));
            }

            Entity ViewOf(Entity texture) => next.Own(Wgpu.CreateTextureView(texture.GetWgpu<WGPUTexture>(), WGPUTextureViewDescriptor.Default));

            var id = UseForward ? default : Target(WGPUTextureFormat.R32Uint, 4, WGPUTextureUsage.RenderAttachment | WGPUTextureUsage.TextureBinding);
            var depth = Target(WGPUTextureFormat.Depth32Float, 4, WGPUTextureUsage.RenderAttachment | WGPUTextureUsage.TextureBinding);
            var hdr = Target(WGPUTextureFormat.RGBA16Float, 8, WGPUTextureUsage.StorageBinding | WGPUTextureUsage.TextureBinding | WGPUTextureUsage.RenderAttachment | WGPUTextureUsage.CopySrc);
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
            var normalRoughness = _owner.HasSurfaceData
                ? Target(WGPUTextureFormat.RGBA16Float, 8, WGPUTextureUsage.StorageBinding | WGPUTextureUsage.TextureBinding | WGPUTextureUsage.CopySrc)
                : default;
            var baseMetallic = _owner.HasSurfaceData
                ? Target(WGPUTextureFormat.RGBA8Unorm, 4, WGPUTextureUsage.StorageBinding | WGPUTextureUsage.TextureBinding | WGPUTextureUsage.CopySrc)
                : default;
            var reflectionInputs = _owner.Settings.SceneReflections
                ? Target(WGPUTextureFormat.RGBA32Float, 16, WGPUTextureUsage.StorageBinding | WGPUTextureUsage.TextureBinding | WGPUTextureUsage.CopySrc, 2)
                : default;
            Entity reflection = default, reflectionRadiance = default, reflectionComposite = default;
            Entity filtered = default, history = default, previousWorld = default, previousNormal = default, temporal = default;
            if (_owner.Settings.SceneReflections) {
                var probes = _owner.Probes!;
                var entries = new List<WGPUBindGroupEntry> {
                    GpuBinding.Texture(1, ViewOf(normalRoughness).GetWgpu<WGPUTextureView>()),
                    GpuBinding.Texture(2, ViewOf(reflectionInputs).GetWgpu<WGPUTextureView>()),
                    GpuBinding.Buffer(3, probes.Tracing),
                    GpuBinding.Buffer(5, _reflectionUniform)
                };
                if (probes.DynamicTracing.IsValid) entries.Add(GpuBinding.Buffer(4, probes.DynamicTracing));
                reflection = GpuBinding.Group(next, _owner.Pipelines.ReflectionLayout, CollectionsMarshal.AsSpan(entries));
                reflectionRadiance = Target(WGPUTextureFormat.RGBA32Float, 16,
                    WGPUTextureUsage.RenderAttachment | WGPUTextureUsage.TextureBinding | WGPUTextureUsage.CopySrc);
                if (_owner.Settings.TemporalReflections) {
                    filtered = Target(WGPUTextureFormat.RGBA16Float, 8,
                        WGPUTextureUsage.RenderAttachment | WGPUTextureUsage.TextureBinding | WGPUTextureUsage.CopySrc);
                    history = Target(WGPUTextureFormat.RGBA16Float, 8, WGPUTextureUsage.TextureBinding | WGPUTextureUsage.CopyDst);
                    previousWorld = Target(WGPUTextureFormat.RGBA32Float, 16, WGPUTextureUsage.TextureBinding | WGPUTextureUsage.CopyDst);
                    previousNormal = Target(WGPUTextureFormat.RGBA16Float, 8, WGPUTextureUsage.TextureBinding | WGPUTextureUsage.CopyDst);
                    temporal = GpuBinding.Group(next, _owner.Pipelines.ReflectionTemporalLayout, [
                        GpuBinding.Texture(1, ViewOf(normalRoughness).GetWgpu<WGPUTextureView>()),
                        GpuBinding.Texture(2, ViewOf(reflectionInputs).GetWgpu<WGPUTextureView>()),
                        GpuBinding.Texture(6, ViewOf(reflectionRadiance).GetWgpu<WGPUTextureView>()),
                        GpuBinding.Texture(7, ViewOf(history).GetWgpu<WGPUTextureView>()),
                        GpuBinding.Texture(8, ViewOf(previousWorld).GetWgpu<WGPUTextureView>()),
                        GpuBinding.Texture(9, ViewOf(previousNormal).GetWgpu<WGPUTextureView>()),
                        GpuBinding.Buffer(10, _reflectionHistoryUniform),
                        GpuBinding.Buffer(11, _owner.Scene.Instances),
                        GpuBinding.Buffer(12, _reflectionPreviousInstances)
                    ]);
                }
                reflectionComposite = GpuBinding.Group(next, _owner.Pipelines.ReflectionCompositeLayout, [
                    GpuBinding.Texture(1, ViewOf(normalRoughness).GetWgpu<WGPUTextureView>()),
                    GpuBinding.Texture(2, ViewOf(reflectionInputs).GetWgpu<WGPUTextureView>()),
                    GpuBinding.Buffer(5, _reflectionUniform),
                    GpuBinding.Texture(6, ViewOf(_owner.Settings.TemporalReflections ? filtered : reflectionRadiance).GetWgpu<WGPUTextureView>())
                ]);
            }
            Entity resolve = default;
            if (!UseForward) {
                var entries = new List<WGPUBindGroupEntry> {
                    GpuBinding.Texture(0, idView.GetWgpu<WGPUTextureView>()),
                    GpuBinding.Texture(1, depthView.GetWgpu<WGPUTextureView>()),
                    GpuBinding.Buffer(2, tiles),
                    GpuBinding.Texture(3, hdrView.GetWgpu<WGPUTextureView>())
                };
                if (_owner.HasSurfaceData) {
                    entries.Add(GpuBinding.Texture(4, ViewOf(normalRoughness).GetWgpu<WGPUTextureView>()));
                    entries.Add(GpuBinding.Texture(5, ViewOf(baseMetallic).GetWgpu<WGPUTextureView>()));
                }
                if (_selection is not null) entries.Add(GpuBinding.Buffer(6, _selection.Work));
                if (_owner.Settings.SceneReflections)
                    entries.Add(GpuBinding.Texture(7, ViewOf(reflectionInputs).GetWgpu<WGPUTextureView>()));
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
            _sizeResources?.Dispose();
            _sizeResources = next;
            _width = width;
            _height = height;
            _id = id;
            _normalRoughness = normalRoughness;
            _baseMetallic = baseMetallic;
            _reflectionInputs = reflectionInputs;
            _reflectionGroup = reflection;
            _reflectionRadiance = reflectionRadiance;
            _reflectionCompositeGroup = reflectionComposite;
            _reflectionFiltered = filtered;
            _reflectionHistory = history;
            _reflectionPreviousWorld = previousWorld;
            _reflectionPreviousNormal = previousNormal;
            _reflectionTemporalGroup = temporal;
            _reflectionHistoryReady = false;
            _depth = depth;
            _hdr = hdr;
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
