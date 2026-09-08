using System.Runtime.InteropServices;
using Sia;
using Sia.Graphics.Reactive;
using Sia.Math;
using Sia.RenderGraph;
using Sia.WebGPU;

namespace Sia.Engine.Rendering.Pbr;

public sealed partial class VisibilityPbrFeature
{
    private static readonly RenderGraphBufferKey s_FixedClustersKey = new("visibility-fixed-clusters");
    private static readonly RenderGraphBufferKey s_ClusterPrefixKey = new("visibility-cluster-prefix");
    private static readonly RenderGraphBufferKey s_ClusterBlocksKey = new("visibility-cluster-blocks");
    private static readonly RenderGraphBufferKey s_ClusterIndicesKey = new("visibility-cluster-indices");

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct FixedClusterGpu(float4 Minimum, float4 Maximum, float4 Sphere, float4 Cone, uint4 Work);

    private readonly record struct FixedGeometryGpu(Entity Source, Entity Indices, Entity Layout, Entity Cull, Entity Scan, Entity Emit,
        uint Count, uint Stride, uint DispatchDimension, HzbGpu Hzb,
        Entity CullLayout, Entity CullMain, Entity CullPost, Entity ScanPost, Entity EmitPost, bool SharedVertices);

    private readonly record struct ClusterViewGpu(Entity Prefix, Entity Blocks, Entity Group, Entity CullGroup);

    private float4 RasterOrigin(float4x4 projection)
    {
        if (_fixedGeometry is null) { return default; }
        var determinant = math.determinant(projection);
        var origin = math.mul(math.inverse(projection), new float4(0, 0, -1, 0));
        if (!float.IsFinite(determinant) || determinant == 0 || !Finite(origin)) { return default; }
        var values = math.abs(origin);
        var scale = System.Math.Max(System.Math.Max(values.x, values.y), System.Math.Max(values.z, values.w));
        return scale > 0 ? origin / scale * (determinant < 0 ? 1 : -1) : default;
    }

    private static unsafe FixedGeometryGpu CreateFixedGeometry(World world, WgpuHandle<WGPUDevice> device,
        WgpuHandle<WGPUQueue> queue, ReadOnlySpan<FixedClusterGpu> clusters, WGPULimits limits, List<Entity> acquired)
    {
        var stride = 1u;
        uint triangles = 0;
        foreach (var cluster in clusters) { stride = System.Math.Max(stride, cluster.Work.z); triangles = checked(triangles + cluster.Work.z); }
        _ = checked((uint)clusters.Length * System.Math.Max(stride * 3u, 256u) * 2u);
        _ = checked(triangles * 3u);
        if (CompactionGroups((uint)clusters.Length) > (ulong)limits.MaxComputeWorkgroupsPerDimension * limits.MaxComputeWorkgroupsPerDimension) {
            throw new ArgumentException("The fixed scene exceeds compute dispatch limits.", nameof(clusters));
        }
        var source = Upload(world, device, queue, clusters, WGPUBufferUsage.Storage, limits, acquired);
        var indices = Allocate(world, device, System.Math.Max(1u, triangles) * 12ul,
            WGPUBufferUsage.Storage | WGPUBufferUsage.Index, limits, acquired);
        WGPUBindGroupLayoutEntry[] entries = [
            BufferLayout(0, WGPUBufferBindingType.Uniform, 160, WGPUShaderStage.Compute),
            BufferLayout(1, WGPUBufferBindingType.ReadOnlyStorage, 80, WGPUShaderStage.Compute),
            BufferLayout(2, WGPUBufferBindingType.ReadOnlyStorage, 192, WGPUShaderStage.Compute),
            BufferLayout(3, WGPUBufferBindingType.Storage, 8, WGPUShaderStage.Compute),
            BufferLayout(4, WGPUBufferBindingType.Storage, 8, WGPUShaderStage.Compute),
            BufferLayout(5, WGPUBufferBindingType.Storage, 8, WGPUShaderStage.Compute),
            BufferLayout(6, WGPUBufferBindingType.Storage, 32, WGPUShaderStage.Compute),
            BufferLayout(7, WGPUBufferBindingType.ReadOnlyStorage, 4, WGPUShaderStage.Compute),
            BufferLayout(8, WGPUBufferBindingType.Storage, 4, WGPUShaderStage.Compute)
        ];
        var layout = Layout(world, device, entries, acquired);
        var cullLayout = Layout(world, device, entries.AsSpan(0, 7), acquired);
        var shader = Own(world, Wgpu.CreateWgslShaderModule(device, PbrShaderSource.LoadVisibilityClusters(), "visibility-clusters"), acquired);
        var pipeline = PipelineLayout(world, device, [layout], acquired);
        var hzb = CreateHzbGpu(world, device, acquired);
        var cullPipeline = PipelineLayout(world, device, [cullLayout, hzb.Layout], acquired);
        return new(source, indices, layout, ComputePipeline(world, device, shader, pipeline, "cull", acquired),
            ComputePipeline(world, device, shader, pipeline, "scan", acquired),
            ComputePipeline(world, device, shader, pipeline, "emit", acquired),
            (uint)clusters.Length, stride, limits.MaxComputeWorkgroupsPerDimension, hzb, cullLayout,
            ComputePipeline(world, device, shader, cullPipeline, "cull_main", acquired),
            ComputePipeline(world, device, shader, cullPipeline, "cull_post", acquired),
            ComputePipeline(world, device, shader, pipeline, "scan_post", acquired),
            ComputePipeline(world, device, shader, pipeline, "emit_post", acquired),
            WgpuUnsafe.wgpuDeviceHasFeature((WGPUDevice*)device.DangerousGetHandle(), WGPUFeatureName.CoreFeaturesAndLimits) != 0);
    }

    private ClusterViewGpu CreateClusterView(FixedGeometryGpu geometry, Entity uniform, Entity work, Entity indirect,
        WGPULimits limits, List<Entity> acquired)
    {
        var device = _device.GetWgpu<WGPUDevice>();
        var prefix = Allocate(_world, device, System.Math.Max(1u, geometry.Count) * 8ul, WGPUBufferUsage.Storage, limits, acquired);
        var blocks = Allocate(_world, device, CompactionGroups(geometry.Count) * 8ul, WGPUBufferUsage.Storage, limits, acquired);
        WGPUBindGroupEntry[] entries = [BufferEntry(0, uniform), BufferEntry(1, geometry.Source),
            BufferEntry(2, _geometry[3]), BufferEntry(3, prefix), BufferEntry(4, blocks),
            BufferEntry(5, work), BufferEntry(6, indirect), BufferEntry(7, _geometry[1]), BufferEntry(8, geometry.Indices)];
        var group = Own(_world, BindGroup(geometry.Layout, entries), acquired);
        var cullGroup = Own(_world, BindGroup(geometry.CullLayout, entries.AsSpan(0, 7)), acquired);
        return new(prefix, blocks, group, cullGroup);
    }

    private sealed partial class ViewState
    {
        public ClusterViewGpu? Clusters { get; init; }

        public void BuildClusterGraph(ref RenderGraphBuildContext graph)
        {
            var geometry = Owner._fixedGeometry!.Value;
            var view = Clusters!.Value;
            ImportBuffer(ref graph, s_FixedClustersKey, geometry.Source, RenderGraphBufferUsage.Storage);
            ImportBuffer(ref graph, s_ClusterPrefixKey, view.Prefix, RenderGraphBufferUsage.Storage);
            ImportBuffer(ref graph, s_ClusterBlocksKey, view.Blocks, RenderGraphBufferUsage.Storage);
            ImportBuffer(ref graph, s_ClusterIndicesKey, geometry.Indices, RenderGraphBufferUsage.Storage | RenderGraphBufferUsage.Index);
            var hzb = Hzb!.Value;
            ImportBuffer(ref graph, s_HzbKey, hzb.Buffer, RenderGraphBufferUsage.Storage | RenderGraphBufferUsage.CopySource);
            ImportBuffer(ref graph, s_HzbParamsKey, hzb.Parameters, RenderGraphBufferUsage.Uniform);
            ImportBuffer(ref graph, s_HzbReduceKey, hzb.ReduceParameters, RenderGraphBufferUsage.Uniform);
            graph.UseComputePass(new("visibility-cluster-culling"), "visibility-cluster-culling", DeclareClusters, CullClusters);
        }

        private static void DeclareClusters(RenderGraphPassDeclarationBuilder declaration) => declaration
                .Read(s_CameraKey, RenderGraphBufferUsage.Uniform).Read(s_FixedClustersKey, RenderGraphBufferUsage.Storage)
                .Read(s_GeometryKeys[3], RenderGraphBufferUsage.Storage)
                .Read(s_HzbKey, RenderGraphBufferUsage.Storage).Read(s_HzbParamsKey, RenderGraphBufferUsage.Uniform)
                .Read(s_GeometryKeys[1], RenderGraphBufferUsage.Storage).Write(s_ClusterIndicesKey, RenderGraphBufferUsage.Storage)
                .ReadWrite(s_ClusterPrefixKey, RenderGraphBufferUsage.Storage).Write(s_ClusterBlocksKey, RenderGraphBufferUsage.Storage)
                .ReadWrite(s_WorkKey, RenderGraphBufferUsage.Storage).ReadWrite(s_IndirectKey, RenderGraphBufferUsage.Storage);

        public void BuildClusterPostGraph(ref RenderGraphBuildContext graph)
        {
            graph.UseComputePass(new("visibility-hzb-main"), "visibility-hzb-main", DeclareHzb, BuildHzb);
            graph.UseComputePass(new("visibility-cluster-post"), "visibility-cluster-post", DeclareClusters, CullPostClusters);
            graph.UsePass(new("visibility-raster-post"), "visibility-raster-post", declaration => {
                DeclarePostRaster(declaration);
                declaration.Read(s_ClusterIndicesKey, RenderGraphBufferUsage.Index);
            }, PostRaster);
            graph.ExportBuffer(s_HzbKey, RenderGraphBufferUsage.Storage);
        }

        public void CullClusters(WgpuReactiveRenderGraphPassContext context) => CullClusters(context, false);
        private void CullPostClusters(WgpuReactiveRenderGraphPassContext context) => CullClusters(context, true);

        private void CullClusters(WgpuReactiveRenderGraphPassContext context, bool post)
        {
            var geometry = Owner._fixedGeometry!.Value;
            var count = CompactionGroups(geometry.Count);
            var width = System.Math.Min(count, geometry.DispatchDimension);
            var height = (count + width - 1) / width;
            var pass = BeginCompute(context);
            try {
                var view = Clusters!.Value;
                Wgpu.SetBindGroup(pass, 0, (Hzb is null ? view.Group : view.CullGroup).GetWgpu<WGPUBindGroup>());
                if (Hzb is { } hzb) { Wgpu.SetBindGroup(pass, 1, hzb.Group.GetWgpu<WGPUBindGroup>()); }
                Wgpu.SetComputePipeline(pass, (Hzb is null ? geometry.Cull : post ? geometry.CullPost : geometry.CullMain).GetWgpu<WGPUComputePipeline>());
                Wgpu.DispatchWorkgroups(pass, width, height);
                Wgpu.SetBindGroup(pass, 0, view.Group.GetWgpu<WGPUBindGroup>());
                Wgpu.SetComputePipeline(pass, (post ? geometry.ScanPost : geometry.Scan).GetWgpu<WGPUComputePipeline>());
                Wgpu.DispatchWorkgroups(pass, 1);
                Wgpu.SetComputePipeline(pass, (post ? geometry.EmitPost : geometry.Emit).GetWgpu<WGPUComputePipeline>());
                var clusters = System.Math.Max(1u, geometry.Count);
                Wgpu.DispatchWorkgroups(pass, System.Math.Min(clusters, geometry.DispatchDimension),
                    System.Math.Min((clusters + geometry.DispatchDimension - 1) / geometry.DispatchDimension, geometry.DispatchDimension));
            }
            finally { Wgpu.EndComputePass(pass); Wgpu.Release(ref pass); }
        }
    }
}
