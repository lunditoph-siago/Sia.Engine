using Sia.Engine.Rendering;
using Xunit;

namespace Sia.Engine.Rendering.Debug.Tests;

public sealed class StreamRangeAvailabilityTests
{
    [Fact]
    public void MaximumFreeRangeReportsContiguousCapacityWithoutMutatingLiveAllocations()
    {
        var pool = new StreamRangePool(21);
        Assert.Equal(21u, pool.MaximumFreeRange);
        Assert.True(pool.TryAllocate(9, out var root));
        var ranges = new StreamRange[4];
        for (var i = 0; i < ranges.Length; i++) Assert.True(pool.TryAllocate(3, out ranges[i]));
        Assert.Equal(0u, pool.MaximumFreeRange);
        pool.Free(ranges[0]);
        pool.Free(ranges[2]);
        Assert.Equal(15u, pool.Used);
        for (var i = 0; i < 8; i++) Assert.Equal(3u, pool.MaximumFreeRange);
        Assert.True(pool.Contains(root));
        Assert.True(pool.Contains(ranges[1]));
        Assert.True(pool.Contains(ranges[3]));
        Assert.False(pool.TryAllocate(6, out _));
        Assert.Equal(15u, pool.Used);
        pool.Free(ranges[1]);
        Assert.Equal(9u, pool.MaximumFreeRange);
        Assert.Throws<InvalidOperationException>(() => pool.Free(ranges[1]));
        Assert.True(pool.TryAllocate(6, out var detail));
        Assert.Equal(9u, detail.Offset);
        Assert.Equal(3u, pool.MaximumFreeRange);
        Assert.False(pool.Contains(ranges[0]));
        pool.Free(detail);
        pool.Free(ranges[3]);
        pool.Free(root);
        Assert.Equal(0u, pool.Used);
        Assert.Equal(21u, pool.MaximumFreeRange);
    }
}
