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
            Entity Target(WGPUTextureFormat format, uint bytes, WGPUTextureUsage usage)
            {
                var desc = WGPUTextureDescriptor.Default;
                desc.Dimension = WGPUTextureDimension._2D;
                desc.Size = new() {
                    Width = width,
                    Height = height,
                    DepthOrArrayLayers = 1
                };
                desc.Format = format;
                desc.Usage = usage;
                return next.Texture(desc, checked((ulong)width * height * bytes));
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
                    GpuBinding.Texture(3, hdrView.GetWgpu<WGPUTextureView>())
                };
                if (_owner.Settings.ExportSurfaceData) {
                    entries.Add(GpuBinding.Texture(4, ViewOf(normalRoughness).GetWgpu<WGPUTextureView>()));
                    entries.Add(GpuBinding.Texture(5, ViewOf(baseMetallic).GetWgpu<WGPUTextureView>()));
                }
                if (_selection is not null) entries.Add(GpuBinding.Buffer(6, _selection.Work));
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
