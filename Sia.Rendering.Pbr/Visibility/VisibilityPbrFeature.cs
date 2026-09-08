using System.Runtime.InteropServices;
using Sia;
using Sia.Engine.Camera;
using Sia.Engine.Mesh;
using Sia.Math;
using Sia.WebGPU;

namespace Sia.Engine.Rendering.Pbr;

public sealed partial class VisibilityPbrFeature :
    IExtractRenderFeature<RenderFrameContext>, IPrepareRenderFeature<RenderFrameContext>, IRenderGraphContributor<RenderFrameContext>
{
    private readonly World _world;
    private readonly Entity _device;
    private readonly Entity _queue;
    private readonly Entity[] _geometry;
    private readonly MeshPatchTree? _patchTree;
    private readonly float4x4[] _transforms;
    private readonly VisibilityLodSettings _lod;
    private readonly LodGpu? _gpuLod;
    private readonly MaterialBatchGpu[] _materialBatches;
    private readonly MaterialTextureGpu[] _materialTextures;
    private readonly Entity _geometryLayout;
    private readonly Entity _resolveLayout;
    private readonly Entity _raster;
    private readonly ResolveGpu _resolve;
    private readonly OutputGpu _output;
    private VisibilityDebugMode _mode;

    public VisibilityDebugMode DebugMode
    {
        get => _mode;
        set {
            if (!Enum.IsDefined(value)) { throw new ArgumentOutOfRangeException(nameof(value)); }
            _mode = value;
        }
    }

    public RenderFeatureKey Key { get; } = new("visibility-pbr");
    public uint TriangleCount { get; }
    public uint InstanceCount { get; private set; }
    public uint InstanceCapacity { get; private set; }
    public uint TriangleCapacity { get; }

    private VisibilityPbrFeature(in GpuFrame frame, Entity[] geometry,
        MaterialBatchGpu[] materials, MaterialTextureGpu[] textures, Entity materialParameters, int materialCount, Entity geometryLayout, Entity resolveLayout,
        Entity raster, ResolveGpu resolve, OutputGpu output, uint triangles, uint capacity, float4x4[] transforms,
        MeshPatchTree? patchTree, VisibilityLodSettings lod, VisibilityDebugMode mode, LodGpu? gpuLod,
        MaterialTilesGpu materialTiles, FixedGeometryGpu? fixedGeometry)
    {
        _world = frame.ResourceWorld;
        _device = frame.Device;
        _queue = frame.Queue;
        _geometry = geometry;
        _patchTree = patchTree;
        _transforms = transforms;
        _lod = lod;
        _gpuLod = gpuLod;
        _fixedGeometry = fixedGeometry;
        _materialTiles = materialTiles;
        _materialBatches = materials;
        _materialParameters = materialParameters;
        MaterialCount = materialCount;
        _materialTextures = textures;
        _geometryLayout = geometryLayout;
        _resolveLayout = resolveLayout;
        _raster = raster;
        _resolve = resolve;
        _output = output;
        TriangleCount = triangles;
        InstanceCount = (uint)transforms.Length;
        InstanceCapacity = InstanceCount;
        TriangleCapacity = capacity;
        _mode = mode;
    }

    public static VisibilityPbrFeature Create(in GpuFrame frame,
        MeshletRasterData geometry, ReadOnlySpan<VisibilityInstance> instances, VisibilityAlbedo albedo,
        WGPUTextureFormat outputFormat, VisibilityDebugMode mode = VisibilityDebugMode.Shaded) =>
        Create(in frame, geometry, instances, albedo, outputFormat, mode, null, default);

    public static VisibilityPbrFeature CreateLod(in GpuFrame frame, MeshPatchTree tree,
        ReadOnlySpan<VisibilityInstance> instances, VisibilityAlbedo albedo, VisibilityLodSettings lod,
        WGPUTextureFormat outputFormat, VisibilityDebugMode mode = VisibilityDebugMode.Shaded) =>
        CreateCpuLod(in frame, tree, instances, albedo, lod, outputFormat, mode);

    public static VisibilityPbrFeature CreateGpuLod(in GpuFrame frame, MeshPatchTree tree,
        ReadOnlySpan<VisibilityInstance> instances, VisibilityAlbedo albedo, VisibilityLodSettings lod,
        WGPUTextureFormat outputFormat, VisibilityDebugMode mode = VisibilityDebugMode.Shaded, bool enableGpuTiming = false)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ValidateLod(lod);
        var scene = CreateScene([tree], instances, lod.Budget);
        return Create(in frame, scene.Geometry, instances, albedo, outputFormat, mode, null, lod, enableGpuTiming, scene);
    }

    private static VisibilityPbrFeature CreateCpuLod(in GpuFrame frame, MeshPatchTree tree,
        ReadOnlySpan<VisibilityInstance> instances, VisibilityAlbedo albedo, VisibilityLodSettings lod,
        WGPUTextureFormat outputFormat, VisibilityDebugMode mode)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ValidateLod(lod);
        var (geometry, meshlets) = tree.CopyGeometry();
        return Create(in frame, MeshletRasterData.Create(geometry, meshlets), instances, albedo, outputFormat, mode, tree, lod);
    }

    private static void ValidateLod(VisibilityLodSettings lod)
    {
        if (!float.IsFinite(lod.TargetPixelError) || lod.TargetPixelError < 0 || lod.Budget.MaxPatches < 0
            || lod.Budget.MaxMeshlets < 0 || lod.Budget.MaxTriangles < 0
            || lod.Budget.MaxRefinementCandidates < 0 || lod.Budget.MaxRefinementNodes < 0) {
            throw new ArgumentOutOfRangeException(nameof(lod));
        }
    }

    private static unsafe VisibilityPbrFeature Create(in GpuFrame frame,
        MeshletRasterData geometry, ReadOnlySpan<VisibilityInstance> instances, VisibilityAlbedo? albedo,
        WGPUTextureFormat outputFormat, VisibilityDebugMode mode, MeshPatchTree? tree, VisibilityLodSettings lod,
        bool enableGpuTiming = false, SceneLodData? scene = null, ReadOnlySpan<PbrMaterialAsset> materials = default,
        FixedClusterGpu[]? fixedClusters = null)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        var sourceMaterials = ValidateMaterials(albedo, materials);
        if (!Enum.IsDefined(mode)) { throw new ArgumentOutOfRangeException(nameof(mode)); }
        if (outputFormat is not (WGPUTextureFormat.RGBA8Unorm or WGPUTextureFormat.BGRA8Unorm
            or WGPUTextureFormat.RGBA8UnormSrgb or WGPUTextureFormat.BGRA8UnormSrgb)) {
            throw new ArgumentOutOfRangeException(nameof(outputFormat));
        }
        var triangles = checked((uint)geometry.Triangles.Length);
        var capacity = scene?.TriangleCapacity ?? WorkCapacity(tree, triangles, (uint)instances.Length, lod.Budget);
        _ = checked(capacity * 3u);
        var gpuInstances = new InstanceGpu[scene?.InstanceCapacity ?? instances.Length];
        var transforms = new float4x4[instances.Length];
        for (var i = 0; i < instances.Length; i++) {
            if (scene is null && instances[i].AssetIndex != 0) {
                throw new ArgumentOutOfRangeException(nameof(instances), "A single geometry input only accepts asset index zero.");
            }
            gpuInstances[i] = ToGpu(instances[i], scene?.InstanceRoots[i] ?? default, sourceMaterials.Length);
            transforms[i] = instances[i].Transform;
        }
        var device = frame.Device.GetWgpu<WGPUDevice>();
        if (enableGpuTiming && WgpuUnsafe.wgpuDeviceHasFeature((WGPUDevice*)device.DangerousGetHandle(), WGPUFeatureName.TimestampQuery) == 0) {
            throw new ArgumentException("GPU timing requires the timestamp-query device feature.", nameof(enableGpuTiming));
        }
        var queue = frame.Queue.GetWgpu<WGPUQueue>();
        var limits = Wgpu.GetLimits(device);
        var workSize = System.Math.Max(1u, (uint?)fixedClusters?.Length ?? capacity) * 8ul;
        if (workSize > limits.MaxBufferSize || workSize > limits.MaxStorageBufferBindingSize) {
            throw new ArgumentException("The required visibility work list exceeds the device capacity.", nameof(instances));
        }
        ValidateTextureCapacity(sourceMaterials, limits);
        var world = frame.ResourceWorld;
        var acquired = new List<Entity>();
        try {
            var buffers = new[] {
                UploadPacked<MeshVertex, PackedVertexGpu>(world, device, queue, geometry.Vertices.Span,
                    PackedVertexGpu.From, limits, acquired),
                Upload(world, device, queue, geometry.Indices.Span, WGPUBufferUsage.Storage, limits, acquired),
                UploadPacked<uint4, TriangleGpu>(world, device, queue, geometry.Triangles.Span,
                    triangle => {
                        var meshlet = geometry.Meshlets.Span[(int)triangle.x];
                        return new(meshlet.x, geometry.Indices.Span[checked((int)(meshlet.y + triangle.y))]);
                    }, limits, acquired),
                Upload<InstanceGpu>(world, device, queue, gpuInstances, WGPUBufferUsage.Storage, limits, acquired)
            };
            var (materialGpu, textures, materialParameters) = CreateMaterials(world, device, queue, sourceMaterials, limits, acquired);
            var geometryLayout = CreateGeometryLayout(world, device, acquired);
            var resolveLayout = CreateResolveLayout(world, device, acquired);
            var raster = CreateRaster(world, device, geometryLayout, acquired, indexed: fixedClusters is not null);
            var resolve = CreateResolve(world, device, geometryLayout, resolveLayout, acquired);
            var materialTiles = CreateMaterialTiles(world, device, acquired);
            var output = CreateOutput(world, device, outputFormat, acquired);
            var gpuLod = scene is not null && fixedClusters is null ? CreateLodGpu(world, device, queue, scene, lod, limits, acquired, enableGpuTiming) : (LodGpu?)null;
            var fixedGeometry = fixedClusters is null ? null : (FixedGeometryGpu?)CreateFixedGeometry(world, device, queue, fixedClusters, limits, acquired);
            return new(in frame, buffers, materialGpu, textures, materialParameters, sourceMaterials.Length, geometryLayout, resolveLayout,
                raster, resolve, output, triangles, capacity, transforms, tree, lod, mode, gpuLod, materialTiles, fixedGeometry) {
                InstanceCapacity = (uint)gpuInstances.Length
            };
        }
        catch {
            for (var i = acquired.Count - 1; i >= 0; i--) { acquired[i].Destroy(); }
            throw;
        }
    }

    public void Prepare(in RenderFeatureContext<RenderFrameContext> context)
        => Prepare(in context, false);

    internal void Prepare(in RenderFeatureContext<RenderFrameContext> context, bool sceneLighting)
    {
        ValidateFrame(in context);
        PrepareInstances(in context);
        var view = context.View.PersistentResources.GetOrAdd(() => CreateView());
        if (!ReferenceEquals(view.Owner, this)) {
            throw new InvalidOperationException("A view cannot reuse state from another visibility feature.");
        }
        if (view.InstanceVersion != _instanceVersion) {
            view.HistoryValid = false;
            view.InstanceVersion = _instanceVersion;
        }
        var viewport = context.Frame.Frame.MainWorld.AcquireAddon<Viewport>().Value;
        if (viewport.Width <= 0 || viewport.Height <= 0) { throw new InvalidOperationException("Visibility requires a nonempty viewport."); }
        view.Width = (uint)viewport.Width;
        view.Height = (uint)viewport.Height;
        view.PrepareMaterialTiles();
        view.Frame = context.Frame;
        view.ResolvePipeline = sceneLighting && _mode == VisibilityDebugMode.Shaded ? _resolve.Surface : _resolve.Debug;
        var camera = context.Frame.Camera.Get<CameraMatrices>();
        if (!Finite(camera.ViewProj)) { throw new ArgumentException("Visibility requires a finite camera projection."); }
        if (_gpuLod is null) { UpdateWork(view, camera.ViewProj); }
        if (_gpuLod is not null || _fixedGeometry is not null) { PrepareOcclusion(view, camera.ViewProj); }
        var uniform = new CameraGpu(camera.ViewProj, new float4(camera.WorldPosition, 1),
            new uint4(view.Width, view.Height, _gpuLod is null ? view.WorkCount : TriangleCapacity, (uint)_mode | (sceneLighting ? 256u : 0u)),
            new float4(math.normalize(new float3(0.4f, 0.8f, 0.6f)), 0), new float4(4, 4, 4, 0), RasterConfig, RasterOrigin(camera.ViewProj));
        Wgpu.WriteBuffer<CameraGpu>(_queue.GetWgpu<WGPUQueue>(), view.Uniform.GetWgpu<WGPUBuffer>(), 0, [uniform]);
    }

    private static bool Finite(float3 value) =>
        float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);

    private static bool Finite(float4 value) =>
        float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z) && float.IsFinite(value.w);

    private static bool Finite(float4x4 value) =>
        Finite(value.c0) && Finite(value.c1) && Finite(value.c2) && Finite(value.c3);

    private static Entity Own<T>(World world, WgpuHandle<T> handle, List<Entity> acquired) where T : unmanaged
    {
        var entity = world.OwnWgpu(handle);
        acquired.Add(entity);
        return entity;
    }

    private static unsafe Entity Upload<T>(World world, WgpuHandle<WGPUDevice> device, WgpuHandle<WGPUQueue> queue,
        ReadOnlySpan<T> data, WGPUBufferUsage usage, WGPULimits limits, List<Entity> acquired) where T : unmanaged
    {
        var size = checked((ulong)System.Math.Max(1, data.Length) * (ulong)sizeof(T));
        if (size > limits.MaxBufferSize || ((usage & WGPUBufferUsage.Storage) != 0 && size > limits.MaxStorageBufferBindingSize)) {
            throw new ArgumentException("The resident geometry/instance buffer exceeds the device binding limit.");
        }
        var entity = Own(world, Wgpu.CreateBuffer(device,
            new WGPUBufferDescriptor { Size = size, Usage = usage | WGPUBufferUsage.CopyDst }), acquired);
        Wgpu.WriteBuffer(queue, entity.GetWgpu<WGPUBuffer>(), 0, data);
        return entity;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct InstanceGpu(float4x4 Transform, float4x4 NormalTransform,
        float4 Color, float4 Material, float4 Emissive, uint4 Roots);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct CameraGpu(float4x4 ViewProjection, float4 Eye, uint4 SizeCounts,
        float4 LightDirection, float4 LightRadiance, uint4 Raster, float4 RasterOrigin);
}
