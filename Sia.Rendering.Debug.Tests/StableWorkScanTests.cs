using Sia.Engine.Rendering.Pbr;
using Xunit;

namespace Sia.Rendering.Debug.Tests;

public sealed class StableWorkScanTests
{
    [Theory]
    [InlineData(0u, uint.MaxValue, 0u)]
    [InlineData(1u, 16u, 0u)]
    [InlineData(1u, 17u, 1u)]
    [InlineData(2u, 32u, 0u)]
    [InlineData(2u, 33u, 1u)]
    [InlineData(3u, 48u, 0u)]
    [InlineData(3u, 49u, 2u)]
    [InlineData(4u, 65u, 2u)]
    [InlineData(130u, 2112u, 0u)]
    [InlineData(130u, 2113u, 65u)]
    [InlineData(134217727u, uint.MaxValue, 0u)]
    [InlineData(134217728u, uint.MaxValue, 0u)]
    [InlineData(uint.MaxValue, uint.MaxValue, 0u)]
    public void UsesOnlyTheTwoDeadFrontiersAndSafeShaderAddresses(uint words, uint nodes, uint blocks)
        => Assert.Equal(blocks, PbrGpuSelection.StableScanBlocks(words, nodes));

    [Fact]
    public void ShadowBudgetFailureFallsBackWithoutAllocatingScanStorage()
    {
        Assert.Equal(0u, PbrGpuSelection.StableScanBlocks(PbrGpuHierarchy.ShadowCacheWords(65, 95), 49));
        Assert.Equal(2u, PbrGpuSelection.StableScanBlocks(PbrGpuHierarchy.ShadowCacheWords(65, 96), 49));
        Assert.Equal(0u, PbrGpuSelection.StableScanBlocks(PbrGpuHierarchy.ShadowCacheWords(65, 96), 48));
    }

    [Fact]
    public void SourceDomainsPreserveInstanceThenLocalNodeOrderAcrossSharedAssets()
    {
        // Two source rows alias the same immutable GPU node range. A third
        // asset's GPU base can change independently of source-domain order.
        ulong count = 0;
        (uint GlobalBase, uint Domain, int Count)[] sources = [
            (100, PbrGpuHierarchy.AppendShadowDomain(ref count, 35), 35),
            (100, PbrGpuHierarchy.AppendShadowDomain(ref count, 35), 35),
            (7, PbrGpuHierarchy.AppendShadowDomain(ref count, 3), 3)];
        var keys = sources.SelectMany((source, instance) => Enumerable.Range(0, source.Count)
            .Select(local => (Instance: instance, Node: source.GlobalBase + (uint)local,
                Ordinal: source.Domain + (uint)local))).Reverse().ToArray();
        Assert.Equal(keys.OrderBy(key => key.Instance).ThenBy(key => key.Node).ToArray(),
            keys.OrderBy(key => key.Ordinal).ToArray());
        Assert.Equal(keys.Length, keys.Select(key => key.Ordinal).Distinct().Count());
    }
}
