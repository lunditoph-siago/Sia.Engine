using Sia;
using Sia.Engine.Mesh;
using Sia.Math;
using Sia.WebGPU;

namespace Sia.Engine.Rendering.Pbr;

public sealed partial class VisibilityPbrFeature
{
    private LodGpu? _shadowLod;
    private uint ShadowTriangleCapacity => _lod.Shadows is { } shadow
        ? System.Math.Min(TriangleCapacity, (uint)shadow.Budget.MaxTriangles) : TriangleCapacity;

    private static void ValidateShadowRoots(uint3 cost, MeshPatchBudget budget)
    {
        if (cost.x > budget.MaxPatches || cost.y > budget.MaxMeshlets || cost.z > budget.MaxTriangles) {
            throw new ArgumentException("The shadow budget cannot hold the complete scene root cut.");
        }
    }

    private LodGpu? CreateShadowLod(List<Entity> acquired)
    {
        if (_gpuLod is not { } lod || _lod.Shadows is not { } shadow) { return null; }
        var data = lod.ParameterData with {
            Budget = new((uint)shadow.Budget.MaxPatches, (uint)shadow.Budget.MaxMeshlets, (uint)shadow.Budget.MaxTriangles,
                BitConverter.SingleToUInt32Bits(shadow.TargetPixelError == 0 ? 0 : shadow.TargetPixelError)),
            Traversal = lod.ParameterData.Traversal with { x = (uint)shadow.Budget.MaxRefinementCandidates, y = (uint)shadow.Budget.MaxRefinementNodes }
        };
        var device = _device.GetWgpu<WGPUDevice>();
        var parameters = Upload<LodParamsGpu>(_world, device, _queue.GetWgpu<WGPUQueue>(), [data], WGPUBufferUsage.Uniform, Wgpu.GetLimits(device), acquired);
        var capacity = (uint)System.Math.Max(1ul, System.Math.Min(lod.Capacity, (ulong)shadow.Budget.MaxPatches + (uint)shadow.Budget.MaxRefinementNodes));
        return lod with { Parameters = parameters, ParameterData = data, Capacity = capacity,
            Parallel = lod.Parallel is { } parallel ? ConfigureParallelLod(parallel, capacity, (uint)shadow.MaxTraversalPasses) : null };
    }

    private void PrepareShadowLod()
    {
        if (_shadowLod is not { } lod || _preparedInstances is not { } instances) { return; }
        var data = lod.ParameterData with {
            Counts = lod.ParameterData.Counts with { y = instances.RootCount, z = (uint)instances.Instances.Length },
            Traversal = lod.ParameterData.Traversal with { z = instances.RootMeshlets, w = instances.RootTriangles }
        };
        if (data == lod.ParameterData) { return; }
        Wgpu.WriteBuffer<LodParamsGpu>(_queue.GetWgpu<WGPUQueue>(), lod.Parameters.GetWgpu<WGPUBuffer>(), 0, [data]);
        _shadowLod = lod with { ParameterData = data };
    }
}
