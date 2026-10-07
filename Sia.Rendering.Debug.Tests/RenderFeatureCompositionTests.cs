using Sia.Engine.Rendering;
using Xunit;

namespace Sia.Engine.Rendering.Debug.Tests;

public sealed class RenderFeatureCompositionTests
{
    private sealed class Feature(string key) : IRenderFeature
    {
        public RenderFeatureKey Key { get; set; } = new(key);
    }

    [Fact]
    public void RemoveAndAppendPreservesRegistrationOrderAmongReadyFeatures()
    {
        for (var count = 3; count <= 32; count++) {
            var builder = new RenderFeaturePipelineBuilder<int>();
            var keys = Enumerable.Range(0, count + 4).Select(i => i.ToString()).ToArray();
            foreach (var key in keys[..count]) builder.Add(new Feature(key));
            Assert.True(builder.Remove(new(keys[0])));
            foreach (var key in keys[count..]) builder.Add(new Feature(key));
            Assert.Equal(keys[1..], builder.Build().Features.Select(f => f.Key.Value));
        }
    }

    [Fact]
    public void FeaturesCannotBeMutatedThroughThePublishedCollection()
    {
        var original = new Feature("original");
        var pipeline = new RenderFeaturePipelineBuilder<int>().Add(original).Build();
        var list = Assert.IsAssignableFrom<IList<IRenderFeature>>(pipeline.Features);
        Assert.True(list.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => list[0] = new Feature("replacement"));
        Assert.Same(original, Assert.Single(pipeline.Features));
    }

    [Fact]
    public void BeforeAndAfterDeclarationsDeduplicateEdgesAndPreserveReadyOrder()
    {
        var builder = new RenderFeaturePipelineBuilder<int>()
            .Add(new Feature("last"), runsAfter: [new("first"), new("first")])
            .Add(new Feature("independent"))
            .Add(new Feature("first"), runsBefore: [new("last")]);
        Assert.Equal(new[] { "independent", "first", "last" },
            builder.Build().Features.Select(f => f.Key.Value));
    }

    [Fact]
    public void RegistrationIdentityIsCapturedOnceAndDependenciesAreCopied()
    {
        var first = new Feature("first");
        var after = new List<RenderFeatureKey> { new("first") };
        var builder = new RenderFeaturePipelineBuilder<int>()
            .Add(new Feature("last"), runsAfter: after).Add(first);
        after.Clear();
        first.Key = new("renamed");
        var pipeline = builder.Build();
        Assert.Same(first, pipeline.Features[0]);
        Assert.Equal("last", pipeline.Features[1].Key.Value);
        Assert.True(builder.Remove(new("first")));
        Assert.Throws<InvalidOperationException>(() => builder.Build());
        Assert.Equal(2, pipeline.Features.Count);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("self")]
    [InlineData("cycle")]
    public void InvalidDependenciesAreRejectedBeforeDispatch(string variant)
    {
        var calls = new List<string>();
        var builder = new RenderFeaturePipelineBuilder<int>();
        if (variant == "cycle") builder.Add(new Stages("second", calls), runsAfter: [new("first")]);
        builder.Add(new Stages("first", calls), runsAfter: [new(variant == "cycle" ? "second" : variant == "self" ? "first" : "absent")]);
        Assert.Throws<InvalidOperationException>(() => builder.Build());
        Assert.Empty(calls);
    }

    private sealed class Stages(string key, List<string> calls) :
        IExtractRenderFeature<int>, IPrepareRenderFeature<int>, IQueueRenderFeature<int>, IRenderGraphContributor<int>
    {
        public RenderFeatureKey Key { get; } = new(key);
        public void Extract(in RenderFeatureContext<int> context) => calls.Add($"{key}:extract:{context.Frame}");
        public void Prepare(in RenderFeatureContext<int> context) => calls.Add($"{key}:prepare:{context.Frame}");
        public void Queue(in RenderFeatureContext<int> context) => calls.Add($"{key}:queue:{context.Frame}");
        public void BuildRenderGraph(ref RenderGraphBuildContext graph, in RenderFeatureContext<int> context) => calls.Add($"{key}:graph:{context.Frame}");
    }

    private sealed class Batch(List<string> calls) : IPrepareRenderFeature<int>
    {
        public RenderFeatureKey Key => new("batch");
        public void Prepare(in RenderFeatureContext<int> context) => throw new InvalidOperationException("Batch override was bypassed.");
        public void PrepareFrame(RenderWorld world, ReadOnlySpan<RenderFeatureContext<int>> contexts) => calls.Add($"batch:{contexts.Length}");
    }

    private sealed class OtherContext : IExtractRenderFeature<string>
    {
        public RenderFeatureKey Key => new("other");
        public void Extract(in RenderFeatureContext<string> context) => throw new InvalidOperationException("Wrong context dispatched.");
    }

    [Fact]
    public void StagesPreserveDependencyOrderAndBuiltPipelineIsIndependentOfBuilder()
    {
        var calls = new List<string>();
        var builder = new RenderFeaturePipelineBuilder<int>()
            .Add(new Stages("last", calls), runsAfter: [new("first")])
            .Add(new OtherContext())
            .Add(new Stages("first", calls));
        var pipeline = builder.Build();
        builder.Remove(new("first"));
        using var world = new RenderWorld();
        var context = new RenderFeatureContext<int>(world, world.GetOrCreateView(new("main")), 7);
        pipeline.Extract(context);
        pipeline.Prepare(context);
        pipeline.Queue(context);
        RenderGraphBuildContext graph = default;
        pipeline.BuildRenderGraph(ref graph, context);
        Assert.Equal(new[] {
            "first:extract:7", "last:extract:7", "first:prepare:7", "last:prepare:7",
            "first:queue:7", "last:queue:7", "first:graph:7", "last:graph:7"
        }, calls);
    }

    [Fact]
    public void BatchPreparationHonorsOverridesAndDefaultPerViewFallback()
    {
        var calls = new List<string>();
        var pipeline = new RenderFeaturePipelineBuilder<int>()
            .Add(new Stages("fallback", calls)).Add(new Batch(calls)).Build();
        using var world = new RenderWorld();
        pipeline.PrepareFrame(world, [
            new(world, world.GetOrCreateView(new("main")), 1),
            new(world, world.GetOrCreateView(new("shadow")), 2)
        ]);
        Assert.Equal(new[] { "fallback:prepare:1", "fallback:prepare:2", "batch:2" }, calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void InvalidBatchFailsBeforeAnyFeatureRuns(bool duplicate)
    {
        var calls = new List<string>();
        var pipeline = new RenderFeaturePipelineBuilder<int>().Add(new Stages("first", calls)).Build();
        using var world = new RenderWorld();
        using var other = new RenderWorld();
        var first = new RenderFeatureContext<int>(world, world.GetOrCreateView(new("main")), 1);
        var second = duplicate ? first : new(other, other.GetOrCreateView(new("other")), 2);
        Assert.Throws<ArgumentException>(() => pipeline.PrepareFrame(world, [first, second]));
        Assert.Empty(calls);
    }

    [Fact]
    public void ProcessFrameCompletesEachStageAcrossAllViewsBeforeStartingTheNext()
    {
        var calls = new List<string>();
        var pipeline = new RenderFeaturePipelineBuilder<int>()
            .Add(new Stages("last", calls), runsAfter: [new("first")])
            .Add(new Batch(calls)).Add(new OtherContext()).Add(new Stages("first", calls)).Build();
        using var world = new RenderWorld();
        pipeline.ProcessFrame(world, [
            new(world, world.GetOrCreateView(new("main")), 1),
            new(world, world.GetOrCreateView(new("shadow")), 2)
        ]);
        Assert.Equal(new[] {
            "first:extract:1", "last:extract:1", "first:extract:2", "last:extract:2",
            "batch:2", "first:prepare:1", "first:prepare:2", "last:prepare:1", "last:prepare:2",
            "first:queue:1", "last:queue:1", "first:queue:2", "last:queue:2"
        }, calls);
        Assert.Equal(0ul, world.FrameIndex); // BeginFrame is an explicit caller effect.
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("world")]
    [InlineData("view")]
    [InlineData("removed")]
    public void ProcessFrameRejectsTheEntireBatchBeforeExtraction(string invalid)
    {
        var calls = new List<string>();
        var pipeline = new RenderFeaturePipelineBuilder<int>().Add(new Stages("first", calls)).Build();
        using var world = new RenderWorld();
        using var other = new RenderWorld();
        var first = new RenderFeatureContext<int>(world, world.GetOrCreateView(new("main")), 1);
        var foreign = other.GetOrCreateView(new("other"));
        var removed = world.GetOrCreateView(new("removed"));
        world.RemoveView(removed.Key);
        var second = invalid switch {
            "duplicate" => first,
            "world" => new(other, foreign, 2),
            "view" => new(world, foreign, 2),
            _ => new(world, removed, 2)
        };
        Assert.Throws<ArgumentException>(() => pipeline.ProcessFrame(world, [first, second]));
        Assert.Empty(calls);
    }

    private sealed class FailingPrepare : IPrepareRenderFeature<int>
    {
        public RenderFeatureKey Key => new("failure");
        public void Prepare(in RenderFeatureContext<int> context) => throw new InvalidOperationException("Preparation failed.");
    }

    [Fact]
    public void ProcessFrameStopsBeforeQueueingWhenPreparationFails()
    {
        var calls = new List<string>();
        var pipeline = new RenderFeaturePipelineBuilder<int>()
            .Add(new Stages("first", calls)).Add(new FailingPrepare()).Build();
        using var world = new RenderWorld();
        var context = new RenderFeatureContext<int>(world, world.GetOrCreateView(new("main")), 1);
        Assert.Throws<InvalidOperationException>(() => pipeline.ProcessFrame(world, [context]));
        Assert.Equal(new[] { "first:extract:1", "first:prepare:1" }, calls);
    }

    [Fact]
    public void ProcessFramePreservesEmptyBatchOverridesAndRejectsNullWorld()
    {
        var calls = new List<string>();
        var pipeline = new RenderFeaturePipelineBuilder<int>()
            .Add(new Stages("fallback", calls)).Add(new Batch(calls)).Build();
        Assert.Throws<ArgumentNullException>(() => pipeline.ProcessFrame(null!, []));
        Assert.Empty(calls);
        using var world = new RenderWorld();
        pipeline.ProcessFrame(world, []);
        Assert.Equal(new[] { "batch:0" }, calls);
    }
}
