using Sia.Engine.Rendering.Pbr;
using Sia.Math;
using Xunit;

namespace Sia.Rendering.Debug.Tests;

public sealed class ShadowCutCacheTests
{
    [Fact]
    public void RepeatedSourceTreesGetDisjointInstanceDomains()
    {
        ulong count = 0;
        Assert.Equal(0u, PbrGpuHierarchy.AppendShadowDomain(ref count, 35));
        Assert.Equal(35u, PbrGpuHierarchy.AppendShadowDomain(ref count, 35));
        Assert.Equal(70u, PbrGpuHierarchy.AppendShadowDomain(ref count, 3));
        Assert.Equal(73ul, count);
        Assert.Equal(3u, PbrGpuHierarchy.ShadowCacheWords(count, 96));
        Assert.Equal(0u, PbrGpuHierarchy.ShadowCacheWords(count, 95));
    }

    [Fact]
    public void CacheBudgetIncludesSevenHistoriesAndScratchWithoutWrapping()
    {
        Assert.Equal(1u, PbrGpuHierarchy.ShadowCacheWords(32, 32));
        Assert.Equal(2u, PbrGpuHierarchy.ShadowCacheWords(33, 64));
        Assert.Equal(0u, PbrGpuHierarchy.ShadowCacheWords(33, 63));
        Assert.Equal(0u, PbrGpuHierarchy.ShadowCacheWords(0, ulong.MaxValue));
        Assert.Equal(0u, PbrGpuHierarchy.ShadowCacheWords((ulong)uint.MaxValue + 1, ulong.MaxValue));
        Assert.Equal(0u, PbrGpuHierarchy.ShadowCacheWords(ulong.MaxValue, ulong.MaxValue));
        Assert.Equal(3u, PbrGpuHierarchy.ShadowCacheWords(65, PbrTextureStreamingPolicy.AvailableBytes(1000, 888, 16)));
        Assert.Equal(0u, PbrGpuHierarchy.ShadowCacheWords(65, PbrTextureStreamingPolicy.AvailableBytes(999, 888, 16)));
    }

    [Fact]
    public void TinySelectionCountsActualMinimumBufferAllocations()
    {
        // One node and one page produce 8 logical feedback bytes. The owner
        // allocates 16, so 1536 logical bytes actually require 1544 bytes.
        Assert.Equal(1544ul, PbrGpuHierarchy.SelectionBufferBytes(4, 8, 144));
        Assert.Equal(1472ul, PbrGpuHierarchy.SelectionBufferBytes(0, 0, 144));
        // Uniform/storage sizes also round up to four-byte boundaries.
        Assert.Equal(1616ul, PbrGpuHierarchy.SelectionBufferBytes(4, 17, 145));
        Assert.Throws<OverflowException>(() => PbrGpuHierarchy.SelectionBufferBytes(ulong.MaxValue, 8, 144));
        Assert.Throws<OverflowException>(() => PbrGpuHierarchy.SelectionBufferBytes(4, ulong.MaxValue, 144));
    }

    [Fact]
    public void TinySelectionFallsBackOneByteBelowItsActualCacheCost()
    {
        var mandatory = PbrGpuHierarchy.SelectionBufferBytes(4, 8, 144);
        const ulong cacheBytes = 32; const ulong clearArguments = 16; const ulong dummyBytes = 16;
        Assert.Equal(0u, PbrGpuHierarchy.ShadowCacheWords(1,
            PbrTextureStreamingPolicy.AvailableBytes(1591, mandatory, clearArguments)));
        Assert.True(mandatory + dummyBytes <= 1591);
        Assert.Equal(1u, PbrGpuHierarchy.ShadowCacheWords(1,
            PbrTextureStreamingPolicy.AvailableBytes(1592, mandatory, clearArguments)));
        Assert.Equal(1592ul, mandatory + cacheBytes + clearArguments);
    }

    [Fact]
    public void OversizedDomainsFallBackAndArithmeticOverflowDoesNotAliasAnInstance()
    {
        ulong count = uint.MaxValue - 1ul;
        Assert.Equal(0u, PbrGpuHierarchy.AppendShadowDomain(ref count, 2));
        Assert.Equal((ulong)uint.MaxValue + 1, count);
        Assert.Equal(0u, PbrGpuHierarchy.ShadowCacheWords(count, ulong.MaxValue));
        count = ulong.MaxValue;
        Assert.Throws<OverflowException>(() => PbrGpuHierarchy.AppendShadowDomain(ref count, 1));
        Assert.Equal(ulong.MaxValue, count);
    }

    [Fact]
    public void CpuForceCoversInitialDepthMatrixAndPoseChanges()
    {
        var matrix = float4x4.identity;
        var moved = new float4x4(matrix.c0, matrix.c1, matrix.c2, new float4(1, 0, 0, 1));
        Assert.True(PbrView.NeedsShadowUpdate(false, matrix, matrix, 4, 4));
        Assert.False(PbrView.NeedsShadowUpdate(true, matrix, matrix, 4, 4));
        Assert.True(PbrView.NeedsShadowUpdate(true, matrix, moved, 4, 4));
        Assert.True(PbrView.NeedsShadowUpdate(true, matrix, matrix, 4, 5));
    }

    [Fact]
    public void ExactCutOracleIgnoresOrderingAndImmutablePageRelocation()
    {
        var first = Cut((100, 0, 12), (134, 0, 24), (100, 1, 36));
        var reorderedAndReinstalled = Cut((100, 1, 900), (100, 0, 400), (134, 0, 800));
        Assert.Equal(first, reorderedAndReinstalled);
        Assert.False(Changed(first, reorderedAndReinstalled));
        Assert.True(Changed(first, reorderedAndReinstalled, force: true));
    }

    [Fact]
    public void ExactCutOracleDetectsEqualCountDifferentNodesInstancesAndEmptyCut()
    {
        var first = Cut((100, 0, 12), (101, 0, 24));
        Assert.True(Changed(first, Cut((100, 0, 12), (102, 0, 24))));
        Assert.True(Changed(first, Cut((100, 1, 12), (101, 1, 24))));
        var empty = Cut();
        Assert.True(Changed(first, empty));
        Assert.False(Changed(empty, empty));
        Assert.True(Changed(empty, empty, force: true));
    }

    [Fact]
    public void ExactCutOracleKeepsSevenLayerHistoriesSeparate()
    {
        var histories = Enumerable.Range(0, 7).Select(_ => Cut()).ToArray();
        histories[2] = Cut((100, 0, 12));
        histories[6] = Cut((500, 2, 24));
        Assert.False(Changed(histories[2], Cut((100, 0, 500))));
        Assert.False(Changed(histories[6], Cut((500, 2, 600))));
        Assert.True(Changed(histories[0], Cut((100, 0, 12))));
        Assert.True(Changed(histories[2], Cut((500, 2, 24))));
    }

    // CPU reference fixtures for subsequent hardware comparison. These tests
    // cover domain identity and expected decisions; they do not execute WGSL,
    // indirect commands, or validate depth rasterization on a GPU.
    private static uint[] Cut(params (uint Node, uint Instance, uint Allocation)[] selected)
    {
        ulong nodes = 0;
        var domains = new (uint Source, uint Base, uint Count)[] {
            (100, PbrGpuHierarchy.AppendShadowDomain(ref nodes, 35), 35),
            (100, PbrGpuHierarchy.AppendShadowDomain(ref nodes, 35), 35),
            (500, PbrGpuHierarchy.AppendShadowDomain(ref nodes, 3), 3)
        };
        var words = new uint[checked((int)((nodes + 31) / 32))];
        foreach (var item in selected) {
            var domain = domains[item.Instance];
            Assert.InRange(item.Node, domain.Source, domain.Source + domain.Count - 1);
            var ordinal = checked(domain.Base + (item.Node - domain.Source));
            words[ordinal / 32] |= 1u << (int)(ordinal % 32);
        }
        return words;
    }

    private static bool Changed(uint[] previous, uint[] current, bool force = false)
        => force || !previous.AsSpan().SequenceEqual(current);
}
