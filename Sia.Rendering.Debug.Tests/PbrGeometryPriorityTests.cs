using Sia.Engine.Mesh;
using Sia.Engine.Rendering.Pbr;
using Sia.Math;
using Xunit;
using Request = Sia.Engine.Rendering.Pbr.PbrStreamResidency.ChildRequest;
using Page = Sia.Engine.Rendering.Pbr.PbrStreamResidency.BudgetPage;

namespace Sia.Rendering.Debug.Tests;

public sealed class PbrGeometryPriorityTests
{
    [Fact]
    public void MainCameraGetsTheLastPageBeforeAStrongerShadowOnlyDemand()
    {
        var layout = Layout(Branch(0, 1), Branch(2, 3));
        var shadow = new Request(-1, 0, 1); var main = new Request(-1, 1, 1);
        var requests = new Dictionary<Request, float> { [shadow] = 1e20f, [main] = 2 };
        var counts = layout.Plan(requests, [new(-1, 0, 0), new(-1, 2, 0)], 2, 2, 3, 3, 1,
            mainRequests: new HashSet<Request> { main });
        Assert.Equal(1, counts.Denied);
        Assert.Equal(new[] { 0, 2, 3 }, layout.Result.ToArray().Select(page => page.Page));
        Assert.Equal(2, layout.Result[^1].Priority);
    }

    [Fact]
    public void MainDescendantRetainsItsObservedAncestorClosureBeforeUnrelatedShadows()
    {
        var bounds = new Aabb(new float3(-1), new float3(1));
        var chain = new PbrSceneStream.HierarchyInfo(1, [
            new(bounds, 2, -1, 1, 1, 0, 1, 1),
            new(bounds, 1, 0, 2, 1, 1, 1, 1),
            new(bounds, 0, 1, 3, 0, 2, 1, 1)], [new(0, 0, 1), new(1, 0, 1), new(2, 0, 1)]);
        var layout = Layout(chain, Branch(3, 4));
        var parent = new Request(-1, 0, 1); var main = new Request(-1, 0, 2); var shadow = new Request(-1, 1, 1);
        var requests = new Dictionary<Request, float> { [parent] = 1, [main] = 2, [shadow] = 1e20f };
        var counts = layout.Plan(requests, [new(-1, 0, 0), new(-1, 3, 0)], 2, 2, 4, 4, 1,
            mainRequests: new HashSet<Request> { main });
        Assert.Equal(1, counts.Denied); Assert.Equal(0, counts.MissingAncestors);
        Assert.Equal(new[] { 0, 1, 2, 3 }, layout.Result.ToArray().Select(page => page.Page).Order());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RawPixelErrorOrderingIsPreservedWithinEitherBand(bool mainView)
    {
        var layout = Layout(Branch(0, 1), Branch(2, 3));
        var lower = new Request(-1, 0, 1); var higher = new Request(-1, 1, 1);
        var requests = new Dictionary<Request, float> { [lower] = 1, [higher] = MathF.BitIncrement(1) };
        layout.Plan(requests, [new(-1, 0, 0), new(-1, 2, 0)], 2, 2, 3, 3, 1,
            mainRequests: mainView ? new HashSet<Request> { lower, higher } : null);
        Assert.Equal(new[] { 0, 2, 3 }, layout.Result.ToArray().Select(page => page.Page));
        Assert.Equal(MathF.BitIncrement(1), layout.Result[^1].Priority);
    }

    [Fact]
    public void AShadowOnlyFrameDoesNotInheritLastFramesCameraBand()
    {
        var layout = Layout(Branch(0, 1), Branch(2, 3));
        var shadow = new Request(-1, 0, 1); var previousMain = new Request(-1, 1, 1);
        var requests = new Dictionary<Request, float> { [shadow] = 100, [previousMain] = 2 };
        Page[] roots = [new(-1, 0, 0), new(-1, 2, 0)];
        layout.Plan(requests, roots, 2, 2, 3, 3, 1, mainRequests: new HashSet<Request> { previousMain });
        Assert.Equal(3, layout.Result[^1].Page);
        layout.Plan(requests, roots, 2, 2, 3, 3, 2);
        Assert.Equal(1, layout.Result[^1].Page);
    }

    [Fact]
    public void AShadowSiblingDoesNotInflateItsGroupsMainCameraPriority()
    {
        var bounds = new Aabb(new float3(-1), new float3(1));
        var siblings = new PbrSceneStream.HierarchyInfo(1, [new(bounds, 1, -1, 1, 2, 0, 1, 1),
            new(bounds, 0, 0, 3, 0, 1, 1, 1), new(bounds, 0, 0, 3, 0, 2, 1, 1)],
            [new(0, 0, 1), new(1, 0, 1), new(2, 0, 1)]);
        var layout = Layout(siblings, Branch(3, 4));
        var weakerMain = new Request(-1, 0, 1); var shadowSibling = new Request(-1, 0, 2);
        var strongerMain = new Request(-1, 1, 1);
        var requests = new Dictionary<Request, float> { [weakerMain] = 2, [shadowSibling] = 1e20f, [strongerMain] = 3 };
        layout.Plan(requests, [new(-1, 0, 0), new(-1, 3, 0)], 2, 2, 4, 4, 1,
            mainRequests: new HashSet<Request> { weakerMain, strongerMain });
        Assert.Equal(new[] { 0, 3, 4 }, layout.Result.ToArray().Select(page => page.Page));
        Assert.Equal(3, layout.Result[^1].Priority);
    }

    [Fact]
    public void UnsignedGpuAggregationRetainsMainPixelErrorWithoutShadowContamination()
    {
        var shadow = PbrGeometryFeedback.Encode(1e30f, false);
        var main = PbrGeometryFeedback.Encode(.25f, true);
        var strongerMain = PbrGeometryFeedback.Encode(MathF.BitIncrement(.25f), true);
        var result = PbrGeometryFeedback.Decode(System.Math.Max(shadow, System.Math.Max(main, strongerMain)));
        Assert.True(result.MainView); Assert.Equal(MathF.BitIncrement(.25f), result.Priority);
        var shadowOnly = PbrGeometryFeedback.Decode(System.Math.Max(
            PbrGeometryFeedback.Encode(4, false), PbrGeometryFeedback.Encode(8, false)));
        Assert.False(shadowOnly.MainView); Assert.Equal(8, shadowOnly.Priority);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(0x80000000u)]
    [InlineData(0x7f800000u)]
    [InlineData(0xff800000u)]
    [InlineData(0x7fc00001u)]
    public void FeedbackRejectsEmptyOrNonFinitePriorityWords(uint word)
        => Assert.Throws<InvalidDataException>(() => PbrGeometryFeedback.Decode(word));

    private static PbrStreamResidency.BudgetWorkspace Layout(params PbrSceneStream.HierarchyInfo[] trees)
    {
        var count = trees.SelectMany(tree => tree.Parts).Max(part => part.Page) + 1;
        var pages = Enumerable.Range(0, count).Select(page => new PbrSceneStream.PageInfo(page.ToString(), 1, 1, 1, 1)).ToArray();
        return new(trees, Enumerable.Range(0, count).ToArray(), pages, true);
    }

    private static PbrSceneStream.HierarchyInfo Branch(int rootPage, int leafPage)
    {
        var bounds = new Aabb(new float3(-1), new float3(1));
        return new(1, [new(bounds, 1, -1, 1, 1, 0, 1, 1), new(bounds, 0, 0, 2, 0, 1, 1, 1)],
            [new(rootPage, 0, 1), new(leafPage, 0, 1)]);
    }
}
