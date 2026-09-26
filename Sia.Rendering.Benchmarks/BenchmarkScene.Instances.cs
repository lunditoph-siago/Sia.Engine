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
        // A zero refinement budget must preserve every root, even at exact capacity.
        var roots = tree.Nodes.Span[..tree.RootCount].ToArray();
        const int instanceCount = 16;
        var rootTriangles = roots.Sum(root => root.TriangleCount) * instanceCount;
        var rootMeshlets = roots.Sum(root => root.MeshletCount) * instanceCount;
        using var complete = new BenchmarkScene(gpu, tree, Enumerable.Repeat(source, instanceCount).ToArray(), 128, 128,
            new(tree.RootCount * instanceCount, rootMeshlets, rootTriangles) { MaxRefinementCandidates = 0, MaxRefinementNodes = 0 });
        await foreach (var sample in complete.RenderAsync(Enumerable.Repeat(float4x4.identity, 4))) {
            if (sample.Counters.SelectedPatches != tree.RootCount * instanceCount
                || sample.Counters.SelectedMeshlets != rootMeshlets || sample.Counters.SelectedTriangles != rootTriangles)
                throw new InvalidOperationException("Zero refinement budget omitted part of the complete root cut.");
        }
        Console.WriteLine("GPU Scene verification passed: static zero uploads, direct ref updates, removal/reuse, failed extraction recovery and complete root coverage at exact capacity.");
    }
}
