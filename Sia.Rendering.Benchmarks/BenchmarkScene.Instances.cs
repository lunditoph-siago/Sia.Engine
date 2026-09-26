using Sia;
using Sia.Engine.Mesh;
using Sia.Engine.Rendering.Pbr;
using Sia.Math;

namespace Sia.Engine.Rendering.Benchmarks;

internal sealed partial class BenchmarkScene
{
    public static async Task VerifyInstancesAsync()
    {
        using var gpu = await GpuDevice.CreateAsync(false);
        var tree = MeshPatchAsset.Cook(Assets.Grid(8)).Build.Tree;
        var source = new VisibilityInstance(float4x4.identity, PbrMaterial.Default);
        using var scene = new BenchmarkScene(gpu, tree, [source with { Transform = float4x4.Translate(new(10, 0, 0)) }, source], 128, 128,
            new(128, 256, 4096) { MaxRefinementCandidates = 128, MaxRefinementNodes = 256 }, retainedInstances: true);
        var entities = new List<Entity>();
        scene._main.Query(Matchers.Of<VisibilityInstance>(), entities, static (in List<Entity> list, Entity entity) => list.Add(entity));
        var entity = entities[1];
        async Task<FrameSample> Frame(int uploads)
        {
            FrameSample? result = null;
            await foreach (var sample in scene.RenderAsync([float4x4.identity])) result = sample;
            if (scene._feature.FrameStatistics.InstanceUploads != uploads)
                throw new InvalidOperationException($"Expected {uploads} instance uploads, got {scene._feature.FrameStatistics.InstanceUploads}.");
            return result!;
        }
        var initial = await Frame(2);
        if (initial.Counters.SelectedTriangles == 0) throw new InvalidOperationException("Fixture has no visible triangles.");
        await Frame(0);
        entity.Set(source with { Material = source.Material with { BaseColor = new(.8f, .2f, .1f) } });
        await Frame(1);
        entities[0].Destroy();
        initial = await Frame(2);
        if (initial.Counters.MainTriangles + initial.Counters.PostTriangles == 0)
            throw new InvalidOperationException("A live instance beyond a vacant slot was not dispatched.");
        entity.Get<VisibilityInstance>() = source with { Material = source.Material with { BaseColor = new(.1f, .3f, .8f) } };
        await Frame(1);
        entity.Get<VisibilityInstance>() = source with { Transform = float4x4.Translate(new(10, 0, 0)) };
        var moved = await Frame(1);
        if (moved.Counters.MainTriangles + moved.Counters.PostTriangles != 0) throw new InvalidOperationException("Moved instance remains visible.");
        entity.Destroy();
        var removed = await Frame(1);
        if (removed.Counters.SelectedTriangles != 0 || scene._feature.InstanceCount != 0) throw new InvalidOperationException("Removed instance still participates in selection.");
        scene._main.Create(HList.From(source));
        var reused = await Frame(1);
        if (reused.Counters.SelectedTriangles != initial.Counters.SelectedTriangles || scene._feature.InstanceCount != 1)
            throw new InvalidOperationException("Reused slot did not restore geometry.");
        await Frame(0);
        var priorEntities = new List<Entity>();
        scene._main.Query(Matchers.Of<VisibilityInstance>(), priorEntities, static (in List<Entity> list, Entity item) => list.Add(item));
        foreach (var prior in priorEntities) prior.Destroy();
        await Frame(priorEntities.Count);
        var recoveryEntity = scene._main.Create(HList.From(source));
        await Frame(1);
        recoveryEntity.Get<VisibilityInstance>() = source with { AssetIndex = 1 };
        try {
            await Frame(0);
            throw new InvalidOperationException("An out-of-range asset index did not throw during extraction.");
        } catch (ArgumentOutOfRangeException) { /* Expected: the invalid extraction attempt must fail without publishing. */ }
        recoveryEntity.Get<VisibilityInstance>() = source with { Transform = float4x4.Translate(new(10, 0, 0)) };
        var recovered = await Frame(1);
        if (recovered.Counters.MainTriangles + recovered.Counters.PostTriangles != 0)
            throw new InvalidOperationException("Extraction did not resume publishing the corrected instance after a prior failed attempt.");
        await Frame(0);

        var sparseMesh = Assets.Grid(8);
        var sparseRoots = Enumerable.Range(0, 16).Select(_ => new MeshPatch(sparseMesh, 0, [])).ToArray();
        var sparseTree = MeshPatchTree.Create(sparseRoots);
        uint rootMeshlets = 0, completeRootTriangles = 0, sampledRootTriangles = 0, sampledStride16Triangles = 0;
        for (var root = 0; root < sparseTree.RootCount; root++) {
            var patch = sparseTree.Nodes.Span[root];
            rootMeshlets = checked(rootMeshlets + (uint)patch.MeshletCount);
            completeRootTriangles = checked(completeRootTriangles + (uint)patch.TriangleCount);
            if ((root & 1) == 0) sampledRootTriangles = checked(sampledRootTriangles + (uint)patch.TriangleCount);
            if (root == 0) sampledStride16Triangles = (uint)patch.TriangleCount;
        }
        if (sampledRootTriangles == 0 || sampledRootTriangles >= completeRootTriangles)
            throw new InvalidOperationException("The root-stride fixture does not reduce its geometry.");
        using var sparse = new BenchmarkScene(gpu, sparseTree, [source], 128, 128,
            new MeshPatchBudget(sparseTree.RootCount, (int)rootMeshlets, (int)sampledRootTriangles) {
                MaxRefinementCandidates = 0, MaxRefinementNodes = 0
            }, retainedInstances: true, rootCutStride: 2);
        if (sparse._feature.TriangleCapacity != sampledRootTriangles)
            throw new InvalidOperationException("Sparse root selection did not reserve only its sampled triangle worklist.");
        FrameSample? sparseSample = null;
        await foreach (var sample in sparse.RenderAsync([float4x4.identity])) sparseSample = sample;
        var selected = sparseSample!.Counters.SelectedTriangles;
        if (selected == 0 || selected > sampledRootTriangles || selected >= completeRootTriangles)
            throw new InvalidOperationException("GPU root-stride selection did not remain inside its reduced worklist budget.");

        using var staticSparse = new BenchmarkScene(gpu, sparseTree, [source], 128, 128,
            new MeshPatchBudget(sparseTree.RootCount, (int)rootMeshlets, (int)sampledStride16Triangles) {
                MaxRefinementCandidates = 0, MaxRefinementNodes = 0
            }, retainedInstances: true, rootCutStride: 16);
        if (staticSparse._feature.TriangleCapacity != sampledStride16Triangles)
            throw new InvalidOperationException("The static root cut did not reserve its sampled triangle count.");
        FrameSample? staticSparseSample = null;
        await foreach (var sample in staticSparse.RenderAsync([float4x4.identity])) staticSparseSample = sample;
        var staticSelected = staticSparseSample!.Counters.MainTriangles;
        if (staticSelected == 0 || staticSelected > sampledStride16Triangles || staticSelected >= completeRootTriangles)
            throw new InvalidOperationException("The static root-cut indirect draw exceeded its sampled geometry budget.");
        Console.WriteLine("GPU Scene verification passed: Set and direct-ref ECS updates, removal/reuse/recovery, GPU half-root selection, and static stride-16 indirect output within exact triangle capacities.");
    }
}
