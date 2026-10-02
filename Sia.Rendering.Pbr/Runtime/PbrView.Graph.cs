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
    private readonly record struct GraphBranch(
        PbrView View,
        GpuResources Targets,
        RenderGraphTextureKey Color,
        RenderGraphTextureKey Depth,
        ulong Resources);

    public void Build(ref RenderGraphBuildContext graph)
        => graph.UseBranch(_prefix, UseForward,
            new GraphBranch(this, _sizeResources!, _frame.ColorTarget, _frame.DepthTarget, _owner.ResourceRevision),
            static (in branch, ref child) => branch.View.BuildCore(ref child));

    private void BuildCore(ref RenderGraphBuildContext graph)
    {
        foreach (var (Key, Value, Usage) in _sceneBuffers)
            Import(ref graph, Key, Value, Usage);
        foreach (var (Key, Value) in _sceneTextures)
            Import(ref graph, Key, Value);
        if (_selection is not null)
            Import(ref graph, _streamDispatchKey, _selection.DispatchArguments,
                RenderGraphBufferUsage.Indirect | RenderGraphBufferUsage.CopyDestination);
        Import(ref graph, _frameKey, _uniform, RenderGraphBufferUsage.Uniform);
        Import(ref graph, _lightsKey, _lightData, RenderGraphBufferUsage.Storage);
        Import(ref graph, _clustersKey, _clusters, RenderGraphBufferUsage.Storage);
        if (_owner.Probes is { } probes) {
            Import(ref graph, _probesKey, probes.Buffer, RenderGraphBufferUsage.Storage);
            Import(ref graph, _probeTextureKey, probes.Texture);
            Import(ref graph, _probeHeaderKey, probes.Header, RenderGraphBufferUsage.Uniform);
            if (probes.Dynamic) {
                Import(ref graph, _traceKey, probes.Tracing, RenderGraphBufferUsage.Storage);
                Import(ref graph, _probeConfigKey, probes.Configuration, RenderGraphBufferUsage.Uniform);
            }
        }
        if (!UseForward) {
            Import(ref graph, _tilesKey, _tiles, RenderGraphBufferUsage.Storage | RenderGraphBufferUsage.Indirect);
            Import(ref graph, _idKey, _id);
        }
        Import(ref graph, _frame.DepthTarget, _depth);
        Import(ref graph, _hdrKey, _hdr);
        if (_owner.Settings.ExportSurfaceData) {
            Import(ref graph, _normalRoughnessKey, _normalRoughness);
            Import(ref graph, _baseMetallicKey, _baseMetallic);
        }
        Import(ref graph, _shadowKey, _shadowAtlas);
        Import(ref graph, _snapshotKey, _snapshot);
        if (_queries.IsValid) {
            Import(ref graph, _timingsKey, _timing, RenderGraphBufferUsage.CopyDestination | RenderGraphBufferUsage.CopySource);
            Import(ref graph, new(_prefix + "query-scratch"), _queryScratch, RenderGraphBufferUsage.QueryResolve | RenderGraphBufferUsage.CopySource);
        }
        var dependency = new Dependencies(this, _frame.ColorTarget, _frame.DepthTarget);
        BuildStreamReset(ref graph, dependency);
        TimingMarker(ref graph, dependency, 0);
        graph.UsePass(new(_prefix + "clusters"), "pbr-clusters", dependency, static (in d, p) =>
            d.View.ReadFrame(p).Write(d.View._clustersKey, RenderGraphBufferUsage.Storage), Cull, RenderGraphPassKind.Compute);
        TimingMarker(ref graph, dependency, 1);
        graph.UsePass(new(_prefix + "shadow"), "pbr-shadow", dependency, static (in d, p) =>
            (d.View._selection is null ? d.View.ReadFrame(p) : d.View.WriteStreamWork(d.View.ReadFrame(p)))
                .ReadWrite(d.View._shadowKey, RenderGraphTextureUsage.RenderAttachment), Shadows);
        TimingMarker(ref graph, dependency, 2);
        if (UseForward) {
            TimingMarker(ref graph, dependency, 3);
            TimingMarker(ref graph, dependency, 4);
            graph.UsePass(new(_prefix + "prepass"), "pbr-prepass", dependency,
                static (in d, p) => d.View.ReadFrame(p).Write(d.Depth, RenderGraphTextureUsage.RenderAttachment), Prepass);
            graph.UsePass(new(_prefix + "coverage"), "pbr-alpha-coverage", dependency,
                static (in d, p) => d.View.ReadFrame(p).ReadWrite(d.Depth, RenderGraphTextureUsage.RenderAttachment), AlphaCoverage);
            graph.UsePass(new(_prefix + "background"), "pbr-background", dependency,
                static (in d, p) => d.View.ReadFrame(p).Read(d.Depth, RenderGraphTextureUsage.TextureBinding)
                    .Write(d.View._hdrKey, RenderGraphTextureUsage.StorageBinding), ForwardBackground, RenderGraphPassKind.Compute);
            graph.UsePass(new(_prefix + "forward"), "pbr-forward", dependency,
                static (in d, p) => d.View.ReadLighting(p).Read(d.Depth, RenderGraphTextureUsage.RenderAttachment)
                    .ReadWrite(d.View._hdrKey, RenderGraphTextureUsage.RenderAttachment), Forward);
            TimingMarker(ref graph, dependency, 5);
            TimingMarker(ref graph, dependency, 6);
        }
        else {
            BuildStreamSelection(ref graph, dependency);
            graph.UsePass(new(_prefix + "visibility"), "pbr-visibility", dependency, static (in d, p) =>
                d.View.ReadStreamWork(d.View.ReadFrame(p)).Write(d.View._idKey, RenderGraphTextureUsage.RenderAttachment).Write(d.Depth, RenderGraphTextureUsage.RenderAttachment), Raster);
            BuildStreamFeedback(ref graph, dependency);
            TimingMarker(ref graph, dependency, 5);
            // Measure transport separately from material reconstruction and lighting.
            if (_owner.Probes is { Dynamic: true })
                graph.UsePass(new(_prefix + "probe-update"), "scene-probe-update", dependency,
                    static (in d, p) => p
                        .Read(d.View._traceKey, RenderGraphBufferUsage.Storage)
                        .Read(d.View._probeConfigKey, RenderGraphBufferUsage.Uniform)
                        .Write(d.View._probesKey, RenderGraphBufferUsage.Storage)
                        .Write(d.View._probeTextureKey, RenderGraphTextureUsage.StorageBinding),
                    context => _owner.Probes.Integrate(context.CommandEncoder), RenderGraphPassKind.Compute);
            TimingMarker(ref graph, dependency, 6);
            graph.UsePass(new(_prefix + "tiles"), "pbr-material-tiles", dependency, static (in d, p) =>
                d.View.ReadStreamWork(d.View.ReadFrame(p)).Read(d.View._idKey, RenderGraphTextureUsage.TextureBinding).Write(d.View._tilesKey, RenderGraphBufferUsage.Storage), Tiles, RenderGraphPassKind.Compute);
            graph.UsePass(new(_prefix + "shade"), "pbr-fused-shading", dependency, static (in d, p) =>
                d.View.WriteSurface(d.View.ReadStreamWork(d.View.ReadLighting(p)).Read(d.View._idKey, RenderGraphTextureUsage.TextureBinding).Read(d.Depth, RenderGraphTextureUsage.TextureBinding)
                    .Read(d.View._tilesKey, RenderGraphBufferUsage.Storage | RenderGraphBufferUsage.Indirect).Write(d.View._hdrKey, RenderGraphTextureUsage.StorageBinding)), Shade, RenderGraphPassKind.Compute);
        }
        TimingMarker(ref graph, dependency, 7);
        if (_owner.Materials.HasTransmission)
            graph.UsePass(new(_prefix + "snapshot"), "pbr-opaque-snapshot", dependency, static (in d, p) =>
            d.View.ReadTiming(p).Read(d.View._hdrKey, RenderGraphTextureUsage.CopySource).Write(d.View._snapshotKey, RenderGraphTextureUsage.CopyDestination), Snapshot, RenderGraphPassKind.Compute);
        if (_owner.Scene.Transparent.Length != 0)
            graph.UsePass(new(_prefix + "transparent"), "pbr-transparent", dependency, static (in d, p) =>
            d.View.ReadLighting(p).Read(d.Depth, RenderGraphTextureUsage.RenderAttachment | RenderGraphTextureUsage.TextureBinding)
                .Read(d.View._snapshotKey, RenderGraphTextureUsage.TextureBinding).ReadWrite(d.View._hdrKey, RenderGraphTextureUsage.RenderAttachment), Transparency);
        TimingMarker(ref graph, dependency, 8);
        graph.UsePass(new(_prefix + "output"), "pbr-output", dependency, static (in d, p) =>
            d.View.ReadTiming(p).Read(d.View._hdrKey, RenderGraphTextureUsage.TextureBinding).Write(d.Color, RenderGraphTextureUsage.RenderAttachment), Output);
        TimingMarker(ref graph, dependency, 9);
    }

    private RenderGraphPassDeclarationBuilder WriteSurface(RenderGraphPassDeclarationBuilder pass)
    {
        if (_owner.Settings.ExportSurfaceData) {
            pass.Write(_normalRoughnessKey, RenderGraphTextureUsage.StorageBinding);
            pass.Write(_baseMetallicKey, RenderGraphTextureUsage.StorageBinding);
        }
        return pass;
    }

    private void TimingMarker(ref RenderGraphBuildContext graph, Dependencies dependency, int point)
    {
        if (!_queries.IsValid) return;
        graph.UsePass(new(_prefix + "timing/" + point), "pbr-timing-" + point, dependency,
            static (in d, p) => p
                .ReadWrite(d.View._timingsKey, RenderGraphBufferUsage.CopyDestination)
                .ReadWrite(new RenderGraphBufferKey(d.View._prefix + "query-scratch"), RenderGraphBufferUsage.QueryResolve | RenderGraphBufferUsage.CopySource),
            Timestamp, RenderGraphPassKind.Compute);
    }

    private RenderGraphPassDeclarationBuilder ReadTiming(RenderGraphPassDeclarationBuilder p)
    {
        if (_queries.IsValid) p.Read(_timingsKey, RenderGraphBufferUsage.CopySource);
        return p;
    }

    private RenderGraphPassDeclarationBuilder ReadFrame(RenderGraphPassDeclarationBuilder p)
    {
        foreach (var (Key, Value, Usage) in _sceneBuffers)
            p.Read(Key, Usage);
        foreach (var (Key, Value) in _sceneTextures)
            p.Read(Key, RenderGraphTextureUsage.TextureBinding);
        p.Read(_frameKey, RenderGraphBufferUsage.Uniform).Read(_lightsKey, RenderGraphBufferUsage.Storage);
        if (_queries.IsValid) p.Read(_timingsKey, RenderGraphBufferUsage.CopySource);
        return p;
    }

    private RenderGraphPassDeclarationBuilder ReadLighting(RenderGraphPassDeclarationBuilder p)
    {
        ReadFrame(p).Read(_clustersKey, RenderGraphBufferUsage.Storage).Read(_shadowKey, RenderGraphTextureUsage.TextureBinding);
        if (_owner.Probes is not null)
            p.Read(_probeTextureKey, RenderGraphTextureUsage.TextureBinding).Read(_probeHeaderKey, RenderGraphBufferUsage.Uniform);
        return p;
    }

    private static void Import(ref RenderGraphBuildContext graph, RenderGraphBufferKey key, Entity entity, RenderGraphBufferUsage usage)
    {
        graph.UseImportedBuffer(key, new(key.ToString(), entity.Get<WgpuBufferInfo>().Size, usage));
        graph.BindImportedBuffer(key, entity.GetWgpu<WGPUBuffer>());
    }

    private static void Import(ref RenderGraphBuildContext graph, RenderGraphTextureKey key, Entity entity)
    {
        var info = entity.Get<WgpuTextureInfo>();
        graph.UseImportedTexture(key, new(key.ToString(), (RenderGraphTextureFormat)(int)info.Format, info.Size.Width, info.Size.Height,
            dimension: info.Dimension == WGPUTextureDimension._3D ? RenderGraphTextureDimension.D3 : RenderGraphTextureDimension.D2,
            depthOrArrayLayers: info.Size.DepthOrArrayLayers, mipLevelCount: info.MipLevelCount,
            usage: (RenderGraphTextureUsage)(int)info.Usage));
        graph.BindImportedTexture(key, entity.GetWgpu<WGPUTexture>());
    }
}
