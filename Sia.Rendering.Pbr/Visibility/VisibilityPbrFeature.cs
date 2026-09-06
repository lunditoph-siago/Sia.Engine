using System.Runtime.InteropServices;
using Sia;
using Sia.Engine.Camera;
using Sia.Engine.Mesh;
using Sia.Math;
using Sia.WebGPU;

namespace Sia.Engine.Rendering.Pbr;

public sealed partial class VisibilityPbrFeature :
    IPrepareRenderFeature<RenderFrameContext>, IRenderGraphContributor<RenderFrameContext>
{
    private readonly World _world;
    private readonly Entity _device;
    private readonly Entity _queue;
    private readonly Entity[] _geometry;
    private readonly MeshPatchTree? _patchTree;
    private readonly float4x4[] _transforms;
    private readonly VisibilityLodSettings _lod;
    private readonly LodGpu? _gpuLod;
    private readonly Entity _albedoView;
    private readonly Entity _albedoTexture;
    private readonly Entity _sampler;
    private readonly Entity _geometryLayout;
    private readonly Entity _resolveLayout;
    private readonly Entity _raster;
    private readonly Entity _resolve;
    private readonly OutputGpu _output;
    private readonly VisibilityDebugMode _mode;

    public RenderFeatureKey Key { get; } = new("visibility-pbr");
    public uint TriangleCount { get; }
    public uint InstanceCount { get; }
    public uint TriangleCapacity { get; }

    private VisibilityPbrFeature(in GpuFrame frame, Entity[] geometry,
        Entity albedoTexture, Entity albedoView, Entity sampler, Entity geometryLayout, Entity resolveLayout,
        Entity raster, Entity resolve, OutputGpu output, uint triangles, uint capacity, float4x4[] transforms,
        MeshPatchTree? patchTree, VisibilityLodSettings lod, VisibilityDebugMode mode, LodGpu? gpuLod)
    {
        _world = frame.ResourceWorld;
        _device = frame.Device;
        _queue = frame.Queue;
        _geometry = geometry;
        _patchTree = patchTree;
        _transforms = transforms;
        _lod = lod;
        _gpuLod = gpuLod;
        _albedoView = albedoView;
        _albedoTexture = albedoTexture;
        _sampler = sampler;
        _geometryLayout = geometryLayout;
        _resolveLayout = resolveLayout;
        _raster = raster;
        _resolve = resolve;
        _output = output;
        TriangleCount = triangles;
        InstanceCount = (uint)transforms.Length;
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
        CreateLod(in frame, tree, instances, albedo, lod, outputFormat, mode, false);

    public static VisibilityPbrFeature CreateGpuLod(in GpuFrame frame, MeshPatchTree tree,
        ReadOnlySpan<VisibilityInstance> instances, VisibilityAlbedo albedo, VisibilityLodSettings lod,
        WGPUTextureFormat outputFormat, VisibilityDebugMode mode = VisibilityDebugMode.Shaded, bool enableGpuTiming = false) =>
        CreateLod(in frame, tree, instances, albedo, lod, outputFormat, mode, true, enableGpuTiming);

    private static VisibilityPbrFeature CreateLod(in GpuFrame frame, MeshPatchTree tree,
        ReadOnlySpan<VisibilityInstance> instances, VisibilityAlbedo albedo, VisibilityLodSettings lod,
        WGPUTextureFormat outputFormat, VisibilityDebugMode mode, bool gpuSelection, bool enableGpuTiming = false)
    {
        ArgumentNullException.ThrowIfNull(tree);
        if (!float.IsFinite(lod.TargetPixelError) || lod.TargetPixelError < 0 || lod.Budget.MaxPatches < 0
            || lod.Budget.MaxMeshlets < 0 || lod.Budget.MaxTriangles < 0) {
            throw new ArgumentOutOfRangeException(nameof(lod));
        }
        var (geometry, meshlets) = tree.CopyGeometry();
        return Create(in frame, MeshletRasterData.Create(geometry, meshlets), instances, albedo, outputFormat, mode, tree, lod, gpuSelection, enableGpuTiming);
    }

    private static unsafe VisibilityPbrFeature Create(in GpuFrame frame,
        MeshletRasterData geometry, ReadOnlySpan<VisibilityInstance> instances, VisibilityAlbedo albedo,
        WGPUTextureFormat outputFormat, VisibilityDebugMode mode, MeshPatchTree? tree, VisibilityLodSettings lod,
        bool gpuSelection = false, bool enableGpuTiming = false)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(albedo);
        ArgumentNullException.ThrowIfNull(albedo.MipLevels);
        if (!Enum.IsDefined(mode)) { throw new ArgumentOutOfRangeException(nameof(mode)); }
        if (outputFormat is not (WGPUTextureFormat.RGBA8Unorm or WGPUTextureFormat.BGRA8Unorm
            or WGPUTextureFormat.RGBA8UnormSrgb or WGPUTextureFormat.BGRA8UnormSrgb)) {
            throw new ArgumentOutOfRangeException(nameof(outputFormat));
        }
        var triangles = checked((uint)geometry.Triangles.Length);
        var capacity = WorkCapacity(tree, triangles, (uint)instances.Length, lod.Budget);
        _ = checked(capacity * 3u);
        var gpuInstances = new InstanceGpu[instances.Length];
        var transforms = new float4x4[instances.Length];
        for (var i = 0; i < instances.Length; i++) {
            var transform = instances[i].Transform;
            var material = instances[i].Material;
            var determinant = math.determinant(transform);
            if (!Finite(transform) || !float.IsFinite(determinant) || determinant <= 1e-12f
                || transform.c0.w != 0 || transform.c1.w != 0 || transform.c2.w != 0 || transform.c3.w != 1) {
                throw new ArgumentException("Instances require finite, non-singular affine transforms with positive determinant.", nameof(instances));
            }
            if (!Finite(material.BaseColor) || !Finite(material.EmissiveColor)
                || !float.IsFinite(material.Metallic) || material.Metallic is < 0 or > 1
                || !float.IsFinite(material.Roughness) || material.Roughness is < 0 or > 1
                || !float.IsFinite(material.EmissiveStrength) || material.EmissiveStrength < 0) {
                throw new ArgumentException("Instance material parameters are invalid.", nameof(instances));
            }
            var normalTransform = math.transpose(math.inverse(transform));
            var emissive = material.EmissiveColor * material.EmissiveStrength;
            if (!Finite(normalTransform) || !Finite(emissive)) {
                throw new ArgumentException("Instance transforms/materials overflow their GPU representation.", nameof(instances));
            }
            gpuInstances[i] = new(transform, normalTransform,
                new float4(material.BaseColor, 1), new float4(material.Metallic, MathF.Max(material.Roughness, 0.045f), 0, 0),
                new float4(emissive, 0));
            transforms[i] = transform;
        }
        var device = frame.Device.GetWgpu<WGPUDevice>();
        if (enableGpuTiming && WgpuUnsafe.wgpuDeviceHasFeature((WGPUDevice*)device.DangerousGetHandle(), WGPUFeatureName.TimestampQuery) == 0) {
            throw new ArgumentException("GPU timing requires the timestamp-query device feature.", nameof(enableGpuTiming));
        }
        var queue = frame.Queue.GetWgpu<WGPUQueue>();
        var limits = Wgpu.GetLimits(device);
        var workSize = System.Math.Max(1u, capacity) * 16ul;
        if (workSize > limits.MaxBufferSize || workSize > limits.MaxStorageBufferBindingSize) {
            throw new ArgumentException("The required visibility work list exceeds the device capacity.", nameof(instances));
        }
        if (albedo.Width == 0 || albedo.Height == 0 || albedo.Width > limits.MaxTextureDimension2D
            || albedo.Height > limits.MaxTextureDimension2D || albedo.MipLevels.Length == 0) {
            throw new ArgumentException("Albedo dimensions/mips exceed the device limits.", nameof(albedo));
        }
        var width = albedo.Width;
        var height = albedo.Height;
        for (var level = 0; level < albedo.MipLevels.Length; level++) {
            if (albedo.MipLevels[level].Length != checked((long)width * height * 4)
                || (level > 0 && (albedo.Width >> (level - 1)) <= 1 && (albedo.Height >> (level - 1)) <= 1)) {
                throw new ArgumentException("Albedo mip sizes must follow the texture dimensions.", nameof(albedo));
            }
            width = System.Math.Max(1, width / 2);
            height = System.Math.Max(1, height / 2);
        }
        var world = frame.ResourceWorld;
        var acquired = new List<Entity>();
        try {
            var buffers = new[] {
                Upload(world, device, queue, geometry.Vertices.Span, WGPUBufferUsage.Storage, limits, acquired),
                Upload(world, device, queue, geometry.Meshlets.Span, WGPUBufferUsage.Storage, limits, acquired),
                Upload(world, device, queue, geometry.Indices.Span, WGPUBufferUsage.Storage, limits, acquired),
                Upload(world, device, queue, geometry.Triangles.Span, WGPUBufferUsage.Storage, limits, acquired),
                Upload<InstanceGpu>(world, device, queue, gpuInstances, WGPUBufferUsage.Storage, limits, acquired)
            };
            var textureDescriptor = WGPUTextureDescriptor.Default;
            textureDescriptor.Dimension = WGPUTextureDimension._2D;
            textureDescriptor.Size = new WGPUExtent3D { Width = albedo.Width, Height = albedo.Height, DepthOrArrayLayers = 1 };
            textureDescriptor.Format = WGPUTextureFormat.RGBA8Unorm;
            textureDescriptor.Usage = WGPUTextureUsage.TextureBinding | WGPUTextureUsage.CopyDst;
            textureDescriptor.MipLevelCount = (uint)albedo.MipLevels.Length;
            var texture = Own(world, Wgpu.CreateTexture(device, textureDescriptor), acquired);
            width = albedo.Width;
            height = albedo.Height;
            for (var level = 0; level < albedo.MipLevels.Length; level++) {
                var destination = new WGPUTexelCopyTextureInfo {
                    Texture = (WGPUTexture*)texture.GetWgpu<WGPUTexture>().DangerousGetHandle(),
                    MipLevel = (uint)level, Aspect = WGPUTextureAspect.All
                };
                var layout = new WGPUTexelCopyBufferLayout { BytesPerRow = width * 4, RowsPerImage = height };
                var extent = new WGPUExtent3D { Width = width, Height = height, DepthOrArrayLayers = 1 };
                fixed (byte* pixels = albedo.MipLevels[level].Span) {
                    WgpuUnsafe.wgpuQueueWriteTexture((WGPUQueue*)queue.DangerousGetHandle(), &destination,
                        pixels, (nuint)albedo.MipLevels[level].Length, &layout, &extent);
                }
                width = System.Math.Max(1, width / 2);
                height = System.Math.Max(1, height / 2);
            }
            var view = Own(world, Wgpu.CreateTextureView(texture.GetWgpu<WGPUTexture>(), WGPUTextureViewDescriptor.Default), acquired);
            var samplerDescriptor = WGPUSamplerDescriptor.Default;
            samplerDescriptor.AddressModeU = WGPUAddressMode.Repeat;
            samplerDescriptor.AddressModeV = WGPUAddressMode.Repeat;
            samplerDescriptor.MagFilter = WGPUFilterMode.Linear;
            samplerDescriptor.MinFilter = WGPUFilterMode.Linear;
            samplerDescriptor.MipmapFilter = WGPUMipmapFilterMode.Linear;
            samplerDescriptor.LodMaxClamp = albedo.MipLevels.Length - 1;
            var sampler = Own(world, Wgpu.CreateSampler(device, samplerDescriptor), acquired);
            var geometryLayout = CreateGeometryLayout(world, device, acquired);
            var resolveLayout = CreateResolveLayout(world, device, acquired);
            var raster = CreateRaster(world, device, geometryLayout, acquired);
            var resolve = CreateResolve(world, device, geometryLayout, resolveLayout, acquired);
            var output = CreateOutput(world, device, outputFormat, acquired);
            var gpuLod = gpuSelection ? CreateLodGpu(world, device, queue, tree!, (uint)instances.Length, lod, limits, acquired, enableGpuTiming) : (LodGpu?)null;
            return new(in frame, buffers, texture, view, sampler, geometryLayout, resolveLayout,
                raster, resolve, output, triangles, capacity, transforms, tree, lod, mode, gpuLod);
        }
        catch {
            for (var i = acquired.Count - 1; i >= 0; i--) { acquired[i].Destroy(); }
            throw;
        }
    }

    public void Prepare(in RenderFeatureContext<RenderFrameContext> context)
    {
        if (!ReferenceEquals(context.Frame.Frame.ResourceWorld, _world)
            || context.Frame.Frame.Device != _device || context.Frame.Frame.Queue != _queue) {
            throw new InvalidOperationException("The visibility feature belongs to a different resource world/device/queue.");
        }
        var view = context.View.PersistentResources.GetOrAdd(() => CreateView());
        if (!ReferenceEquals(view.Owner, this)) {
            throw new InvalidOperationException("A view cannot reuse state from another visibility feature.");
        }
        var viewport = context.Frame.Frame.MainWorld.AcquireAddon<Viewport>().Value;
        if (viewport.Width <= 0 || viewport.Height <= 0) { throw new InvalidOperationException("Visibility requires a nonempty viewport."); }
        view.Width = (uint)viewport.Width;
        view.Height = (uint)viewport.Height;
        view.Frame = context.Frame;
        var camera = context.Frame.Camera.Get<CameraMatrices>();
        if (!Finite(camera.ViewProj)) { throw new ArgumentException("Visibility requires a finite camera projection."); }
        if (_gpuLod is null) { UpdateWork(view, camera.ViewProj); }
        else { PrepareOcclusion(view, camera.ViewProj); }
        var uniform = new CameraGpu(camera.ViewProj, new float4(camera.WorldPosition, 1),
            new uint4(view.Width, view.Height, _gpuLod is null ? view.WorkCount : TriangleCapacity, (uint)_mode),
            new float4(math.normalize(new float3(0.4f, 0.8f, 0.6f)), 0), new float4(4, 4, 4, 0));
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
        float4 Color, float4 Material, float4 Emissive);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct CameraGpu(float4x4 ViewProjection, float4 Eye, uint4 SizeCounts,
        float4 LightDirection, float4 LightRadiance);
}
