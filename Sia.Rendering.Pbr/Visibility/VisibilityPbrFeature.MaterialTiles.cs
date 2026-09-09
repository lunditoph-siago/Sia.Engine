using Sia;
using Sia.Graphics.Reactive;
using Sia.RenderGraph;
using Sia.WebGPU;

namespace Sia.Engine.Rendering.Pbr;

public sealed partial class VisibilityPbrFeature
{
    private static readonly RenderGraphBufferKey s_MaterialTilesKey = new("visibility-material-tiles");
    private static readonly RenderGraphBufferKey s_MaterialDispatchKey = new("visibility-material-dispatch");
    private readonly MaterialTilesGpu _materialTiles;

    private static MaterialTilesGpu CreateMaterialTiles(World world, WgpuHandle<WGPUDevice> device,
        List<Entity> acquired)
    {
        var geometry = Layout(world, device, [BufferLayout(0, WGPUBufferBindingType.Uniform, 160, WGPUShaderStage.Compute),
            BufferLayout(5, WGPUBufferBindingType.ReadOnlyStorage, 192, WGPUShaderStage.Compute),
            BufferLayout(6, WGPUBufferBindingType.ReadOnlyStorage, 8, WGPUShaderStage.Compute)], acquired);
        var group = Layout(world, device, [TextureLayout(0, WGPUTextureSampleType.Uint, WGPUShaderStage.Compute),
            BufferLayout(1, WGPUBufferBindingType.Storage, 4, WGPUShaderStage.Compute),
            BufferLayout(2, WGPUBufferBindingType.Storage, 12, WGPUShaderStage.Compute),
            BufferLayout(3, WGPUBufferBindingType.ReadOnlyStorage, 80, WGPUShaderStage.Compute)], acquired);
        var layout = PipelineLayout(world, device, [geometry, group], acquired);
        var shader = Own(world, Wgpu.CreateWgslShaderModule(device, PbrShaderSource.LoadVisibilityMaterialTiles()), acquired);
        return new(geometry, group, ComputePipeline(world, device, shader, layout, "reset", acquired),
            ComputePipeline(world, device, shader, layout, "classify", acquired), Wgpu.GetLimits(device).MaxComputeWorkgroupsPerDimension);
    }

    private readonly record struct MaterialTilesGpu(Entity GeometryLayout, Entity Layout, Entity Reset, Entity Classify, uint DispatchDimension);

    private sealed partial class ViewState
    {
        public Entity MaterialGeometryGroup { get; init; }
        private Entity _materialTileBuffer;
        private Entity _materialDispatchBuffer;
        private Entity _materialTileGroup;
        private WgpuHandle<WGPUTextureView> _materialIdView;

        public void PrepareMaterialTiles()
        {
            var size = checked((((ulong)Width + 7) / 8 * (((ulong)Height + 7) / 8) + 1) * (uint)Owner._materialBatches.Length * 4);
            if (_materialTileBuffer.IsValid && Wgpu.GetBufferSize(_materialTileBuffer.GetWgpu<WGPUBuffer>()) == size) { return; }
            var acquired = new List<Entity>();
            var device = Owner._device.GetWgpu<WGPUDevice>();
            var limits = Wgpu.GetLimits(device);
            Entity tiles, dispatch;
            try {
                tiles = Allocate(Owner._world, device, size, WGPUBufferUsage.Storage, limits, acquired);
                dispatch = Allocate(Owner._world, device, (ulong)Owner._materialBatches.Length * 12,
                    WGPUBufferUsage.Storage | WGPUBufferUsage.Indirect, limits, acquired);
            }
            catch { foreach (var entity in acquired) { entity.Destroy(); } throw; }
            foreach (var group in _resolveGroups) { if (group.IsValid) { group.Destroy(); } }
            _resolveGroups = [];
            if (_materialTileGroup.IsValid) { _materialTileGroup.Destroy(); }
            if (_materialTileBuffer.IsValid) { _materialTileBuffer.Destroy(); }
            if (_materialDispatchBuffer.IsValid) { _materialDispatchBuffer.Destroy(); }
            _materialTileGroup = default;
            _materialTileBuffer = tiles;
            _materialDispatchBuffer = dispatch;
        }

        public void BuildMaterialTiles(ref RenderGraphBuildContext graph)
        {
            ImportBuffer(ref graph, s_MaterialTilesKey, _materialTileBuffer, RenderGraphBufferUsage.Storage);
            ImportBuffer(ref graph, s_MaterialDispatchKey, _materialDispatchBuffer, RenderGraphBufferUsage.Storage | RenderGraphBufferUsage.Indirect);
            graph.UseComputePass(new("visibility-material-tiles"), "visibility-material-tiles", DeclareMaterialTiles, ClassifyMaterialTiles);
        }

        private void DeclareMaterialTiles(RenderGraphPassDeclarationBuilder declaration)
        {
            ReadGeometry(declaration);
            declaration.Read(Owner.VisibilityTarget, RenderGraphTextureUsage.TextureBinding)
                .Read(s_MaterialParametersKey, RenderGraphBufferUsage.Storage)
                .Write(s_MaterialTilesKey, RenderGraphBufferUsage.Storage)
                .Write(s_MaterialDispatchKey, RenderGraphBufferUsage.Storage);
        }

        private void ClassifyMaterialTiles(WgpuReactiveRenderGraphPassContext context)
        {
            var id = context.GetTextureView(Owner.VisibilityTarget);
            if (!_materialTileGroup.IsValid || _materialIdView != id) {
                var next = Owner.OwnTextureBindGroup(Owner._materialTiles.Layout,
                    [TextureEntry(0, id), BufferEntry(1, _materialTileBuffer), BufferEntry(2, _materialDispatchBuffer),
                        BufferEntry(3, Owner._materialParameters)], id);
                if (_materialTileGroup.IsValid) { _materialTileGroup.Destroy(); }
                _materialTileGroup = next;
                _materialIdView = id;
            }
            var pass = BeginCompute(context);
            try {
                Wgpu.SetBindGroup(pass, 0, MaterialGeometryGroup.GetWgpu<WGPUBindGroup>());
                Wgpu.SetBindGroup(pass, 1, _materialTileGroup.GetWgpu<WGPUBindGroup>());
                Wgpu.SetComputePipeline(pass, Owner._materialTiles.Reset.GetWgpu<WGPUComputePipeline>());
                Wgpu.DispatchWorkgroups(pass, ((uint)Owner._materialBatches.Length + 63) / 64);
                Wgpu.SetComputePipeline(pass, Owner._materialTiles.Classify.GetWgpu<WGPUComputePipeline>());
                Wgpu.DispatchWorkgroups(pass, (Width + 7) / 8, (Height + 7) / 8);
            }
            finally { Wgpu.EndComputePass(pass); Wgpu.Release(ref pass); }
        }
    }
}
