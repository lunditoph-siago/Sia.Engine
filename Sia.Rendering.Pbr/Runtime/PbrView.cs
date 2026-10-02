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

internal sealed unsafe partial class PbrView : IDisposable
{
    private const uint k_Cells = 16 * 9 * 24;
    private const uint k_LightsPerCell = 64;
    private const uint k_MaximumLights = 256;
    private const uint k_ShadowLayers = 7;

    private static readonly IEntityMatcher s_Casters = Matchers.Of<ShadowCaster>();
    private static readonly IEntityMatcher s_Directionals = Matchers.Of<DirectionalLight, LightColor, GlobalTransform>();
    private static readonly IEntityMatcher s_Points = Matchers.Of<PointLight, LightColor, GlobalTransform>();
    private static readonly IEntityMatcher s_Spots = Matchers.Of<SpotLight, LightColor, GlobalTransform>();

    private readonly record struct Dependencies(
        PbrView View,
        RenderGraphTextureKey Color,
        RenderGraphTextureKey Depth);

    private readonly PbrRenderer _owner;
    private readonly GpuFrame _gpuFrame;
    private readonly GpuResources _gpu;
    private GpuResources? _sizeResources;
    private readonly Entity _uniform, _lightData, _clusters;
    private readonly Entity _shadowAtlas, _shadowArray;
    private readonly Entity _frameGroup, _rasterGroup, _clusterGroup;
    private readonly Entity _outputUniform;
    private readonly Entity _sampler;
    private readonly Entity _depthSampler;
    private readonly Entity[] _shadowViews = new Entity[k_ShadowLayers];
    private readonly bool[] _shadowValid = new bool[k_ShadowLayers];
    private readonly bool[] _shadowDirty = new bool[k_ShadowLayers];
    private readonly float4x4[] _shadowMatrices = new float4x4[k_ShadowLayers];
    private readonly bool[] _shadowActive = new bool[k_ShadowLayers];
    private readonly float4[] _sceneData = new float4[(k_MaximumLights * 4) + (k_ShadowLayers * 4)];
    private readonly List<Entity> _directional = [];
    private readonly HashSet<Entity> _casters = [];
    private readonly EntityHandler _collectCaster, _collectDirectional, _collectPoint, _collectSpot;
    private readonly string _prefix;
    private readonly RenderGraphBufferKey _frameKey, _lightsKey, _clustersKey, _tilesKey;
    private readonly RenderGraphBufferKey _probesKey, _traceKey, _probeConfigKey;
    private readonly RenderGraphBufferKey _probeHeaderKey;
    private readonly RenderGraphTextureKey _probeTextureKey;
    private readonly RenderGraphTextureKey _idKey, _hdrKey, _shadowKey, _snapshotKey;
    private Entity _id, _depth, _hdr, _snapshot;
    private Entity _tiles;
    private Entity _resolveGroup, _tileGroup, _glassGroup, _outputGroup;
    private uint _width, _height;
    private uint _tileStride;
    private uint _spotCount;
    private bool _needsSnapshot;
    private bool _hasVisibilityTargets;
    private Entity _backgroundGroup;
    private Entity _normalRoughness, _baseMetallic;
    private readonly RenderGraphTextureKey _normalRoughnessKey, _baseMetallicKey;
    private PbrFrame _data;
    private RenderFrameContext _frame;
    private readonly List<PbrGpuScene.Draw> _transparent = [];
    private readonly Comparison<PbrGpuScene.Draw> _sortTransparent;
    private float3 _transparentEye;
    private readonly List<PbrGpuScene.Draw> _opaque = [];
    private readonly float[] _materialPixels;
    private readonly List<PbrGpuScene.Draw>[] _shadowDraws = [.. Enumerable.Range(0, (int)k_ShadowLayers).Select(_ => new List<PbrGpuScene.Draw>())];
    private readonly RenderGraphBufferKey _timingsKey;
    private readonly List<(RenderGraphBufferKey Key, Entity Value, RenderGraphBufferUsage Usage)> _sceneBuffers = [];
    private readonly List<(RenderGraphTextureKey Key, Entity Value)> _sceneTextures = [];
    private ulong _materialRevision;
    private readonly Entity _queries, _timing, _queryScratch;

    public RenderView View { get; }
    public uint VisibleTriangles { get; private set; }
    public bool IsDisposed { get; private set; }

    public ulong Bytes => _gpu.Bytes + (_sizeResources?.Bytes ?? 0) + (_selection?.Bytes ?? 0);

    private bool UseForward => _owner.Settings.OpaquePath == PbrOpaquePath.ForwardPlus && _owner.DebugMode == VisibilityDebugMode.Shaded;

    public PbrView(PbrRenderer owner, RenderView view, in GpuFrame frame)
    {
        _owner = owner;
        _sortTransparent = CompareTransparent;
        _materialPixels = new float[owner.Materials.MaterialBatches.Length];
        View = view;
        _gpuFrame = frame;
        _gpu = new(frame, owner.Settings.ViewBytes);
        _prefix = "pbr/" + view.Key.Value + "/";
        _timingsKey = owner.GetGpuTimingsTarget(view.Key);
        _frameKey = new(_prefix + "frame");
        _lightsKey = new(_prefix + "lights");
        _clustersKey = new(_prefix + "clusters");
        _tilesKey = new(_prefix + "tiles");
        _idKey = new(_prefix + "id");
        _hdrKey = new(_prefix + "hdr");
        _shadowKey = new(_prefix + "shadow");
        _snapshotKey = new(_prefix + "opaque-snapshot");
        _normalRoughnessKey = new(_prefix + "normal-roughness");
        _baseMetallicKey = new(_prefix + "base-metallic");
        _probesKey = new(_prefix + "probes");
        _traceKey = new(_prefix + "tracing");
        _probeConfigKey = new(_prefix + "probe-config");
        _probeHeaderKey = new(_prefix + "probe-header");
        _probeTextureKey = new(_prefix + "probe-texture");
        void Buffer(Entity entity, RenderGraphBufferUsage usage) => _sceneBuffers.Add((new(_prefix + "scene-buffer/" + _sceneBuffers.Count), entity, usage));
        void Texture(Entity entity) => _sceneTextures.Add((new(_prefix + "scene-texture/" + _sceneTextures.Count), entity));
        Buffer(owner.Materials.Table, RenderGraphBufferUsage.Storage);
        Buffer(owner.Scene.Vertices, RenderGraphBufferUsage.Storage | RenderGraphBufferUsage.Vertex);
        Buffer(owner.Scene.Topology, RenderGraphBufferUsage.Storage | RenderGraphBufferUsage.Index);
        if (owner.Scene.Instances != default) Buffer(owner.Scene.Instances, RenderGraphBufferUsage.Storage);
        foreach (var entity in owner.Materials.Uniforms)
            Buffer(entity, RenderGraphBufferUsage.Uniform);
        Buffer(owner.Environment.Sh, RenderGraphBufferUsage.Uniform);
        foreach (var entity in owner.Materials.Textures)
            Texture(entity);
        Texture(owner.Environment.Cube);
        Texture(owner.Environment.Lut);
        _collectCaster = e => _casters.Add(e);
        _collectDirectional = e => {
            if (_directional.Count == 4)
                throw new InvalidOperationException("Resident PBR supports at most four directional lights.");
            _directional.Add(e);
        };
        _collectPoint = CollectPoint;
        _collectSpot = CollectSpot;
        try {
            _uniform = _gpu.Buffer(512, WGPUBufferUsage.Uniform | WGPUBufferUsage.CopyDst);
            _lightData = _gpu.Buffer((ulong)_sceneData.Length * 16, WGPUBufferUsage.Storage | WGPUBufferUsage.CopyDst);
            _clusters = _gpu.Buffer(k_Cells * (k_LightsPerCell + 1) * 4, WGPUBufferUsage.Storage);
            _outputUniform = _gpu.Upload<float4>([new(1, owner.OutputFormat is WGPUTextureFormat.BGRA8Unorm or WGPUTextureFormat.RGBA8Unorm ? 1 : 0, 0, 0)], WGPUBufferUsage.Uniform);
            var sampler = WGPUSamplerDescriptor.Default;
            sampler.MinFilter = sampler.MagFilter = WGPUFilterMode.Linear;
            sampler.AddressModeU = sampler.AddressModeV = WGPUAddressMode.ClampToEdge;
            _sampler = _gpu.Own(Wgpu.CreateSampler(_gpu.Device, sampler));
            sampler.MinFilter = sampler.MagFilter = WGPUFilterMode.Nearest;
            sampler.Compare = WGPUCompareFunction.LessEqual;
            _depthSampler = _gpu.Own(Wgpu.CreateSampler(_gpu.Device, sampler));
            var desc = WGPUTextureDescriptor.Default;
            desc.Dimension = WGPUTextureDimension._2D;
            desc.Format = WGPUTextureFormat.Depth32Float;
            desc.Size = new() {
                Width = owner.Settings.ShadowResolution, Height = owner.Settings.ShadowResolution, DepthOrArrayLayers = k_ShadowLayers
            };
            desc.Usage = WGPUTextureUsage.RenderAttachment | WGPUTextureUsage.TextureBinding;
            _shadowAtlas = _gpu.Texture(desc, (ulong)desc.Size.Width * desc.Size.Height * k_ShadowLayers * 4);
            var array = WGPUTextureViewDescriptor.Default;
            array.Dimension = WGPUTextureViewDimension._2DArray;
            _shadowArray = _gpu.Own(Wgpu.CreateTextureView(_shadowAtlas.GetWgpu<WGPUTexture>(), array));
            for (uint i = 0; i < k_ShadowLayers; i++) {
                var layer = WGPUTextureViewDescriptor.Default;
                layer.Dimension = WGPUTextureViewDimension._2D;
                layer.BaseArrayLayer = i;
                layer.ArrayLayerCount = 1;
                _shadowViews[i] = _gpu.Own(Wgpu.CreateTextureView(_shadowAtlas.GetWgpu<WGPUTexture>(), layer));
            }
            _rasterGroup = GpuBinding.Group(_gpu, owner.Pipelines.RasterFrameLayout, [GpuBinding.Buffer(0, _uniform), GpuBinding.Buffer(1, _lightData)]);
            _clusterGroup = GpuBinding.Group(_gpu, owner.Pipelines.ClusterLayout, [GpuBinding.Buffer(0, _uniform), GpuBinding.Buffer(1, _lightData), GpuBinding.Buffer(2, _clusters)]);
            var frameEntries = new List<WGPUBindGroupEntry> {
                GpuBinding.Buffer(0, _uniform),
                GpuBinding.Buffer(1, _lightData),
                GpuBinding.Buffer(2, _clusters),
                GpuBinding.Texture(3, _shadowArray.GetWgpu<WGPUTextureView>()),
                GpuBinding.Texture(4, owner.Environment.CubeView.GetWgpu<WGPUTextureView>()),
                GpuBinding.Texture(5, owner.Environment.LutView.GetWgpu<WGPUTextureView>()),
                GpuBinding.Sampler(6, owner.Environment.Sampler),
                GpuBinding.Sampler(7, owner.Environment.Sampler),
                GpuBinding.Sampler(11, _depthSampler),
                GpuBinding.Buffer(8, owner.Environment.Sh)
            };
            if (owner.Probes is { } probes) {
                frameEntries.Add(GpuBinding.Texture(9, probes.TextureView.GetWgpu<WGPUTextureView>()));
                frameEntries.Add(GpuBinding.Buffer(10, probes.Header));
            }
            _frameGroup = GpuBinding.Group(_gpu, owner.Pipelines.FrameLayout, CollectionsMarshal.AsSpan(frameEntries));
            if (owner.Settings.GpuTiming) {
                var count = (uint)PbrRenderer.GpuTimingStages.Length;
                _queries = _gpu.Own(Wgpu.CreateQuerySet(_gpu.Device, WGPUQueryType.Timestamp, count * 2, "pbr-frame"));
                _queryScratch = _gpu.Buffer(count * 16, WGPUBufferUsage.QueryResolve | WGPUBufferUsage.CopySrc);
                _timing = _gpu.Buffer(count * 16, WGPUBufferUsage.CopyDst | WGPUBufferUsage.CopySrc);
            }
            InitializeGpuSelection();
        }
        catch {
            _selection?.Dispose();
            _gpu.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true;
        _selection?.Dispose();
        _sizeResources?.Dispose();
        _gpu.Dispose();
        _owner.Forget(this);
    }
}
