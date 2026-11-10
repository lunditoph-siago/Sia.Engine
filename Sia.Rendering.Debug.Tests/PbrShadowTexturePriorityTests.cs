using Sia.Engine.Rendering.Pbr;
using Xunit;

namespace Sia.Rendering.Debug.Tests;

public sealed class PbrShadowTexturePriorityTests
{
    [Fact]
    public void LargeShadowFootprintCannotEvictMainVisibleDetail()
    {
        PbrTextureStreamingPolicy.BudgetInput[] inputs = [
            new([100, 10], [0], [1], [1], [0]),
            new([100, 10], [0], [0], [63], [1])];
        var targets = PbrTextureStreamingPolicy.BudgetTargets(inputs, 120);
        Assert.Equal(0, targets[0][0]); Assert.Equal(1, targets[1][0]);
    }

    [Fact]
    public void SharedMainTextureDoesNotPromoteItsExtraShadowMip()
    {
        PbrTextureStreamingPolicy.BudgetInput[] inputs = [
            new([100, 40, 10], [0], [0], [63], [1]),
            new([60, 10], [0], [1], [1], [0])];
        var targets = PbrTextureStreamingPolicy.BudgetTargets(inputs, 150);
        Assert.Equal(1, targets[0][0]); Assert.Equal(0, targets[1][0]);
    }

    [Theory]
    [InlineData(0, 2, 4, 2)]
    [InlineData(0, 2, 2, 0)]
    [InlineData(1, 2, 4, 2)]
    [InlineData(0, 4, 4, 0)]
    public void MainNeededMipPublishesBeforeAnyAdditionalShadowRefinement(int desired, int main, int resident, int expected)
        => Assert.Equal(expected, PbrTextureStreamingPolicy.RefinementTarget(desired, main, resident));
}
