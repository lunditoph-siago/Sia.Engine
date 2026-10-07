using Sia.Engine.Example;
using Xunit;

namespace Sia.Engine.Rendering.Debug.Tests;

public sealed class GpuTimestampClockTests
{
    [Fact]
    public void DeviceTicksConvertBeforeFeedingResolutionFeedback()
    {
        var clock = new GpuTimestampClock(52.083333333333336);
        Assert.Equal(20d, clock.Milliseconds(100, 384100), 10);
        var controller = new RenderResolutionController(10, 1);
        controller.Observe(16, 18, 1, clock.Milliseconds(100, 384100));
        controller.Observe(32, 34, 1, clock.Milliseconds(100, 384100));
        Assert.Equal(.625f, controller.Scale);
        Assert.Equal(1, controller.Changes);
    }

    [Fact]
    public void LargeAbsoluteCountersKeepShortIntervalsAndZeroDuration()
    {
        var clock = new GpuTimestampClock(1);
        const ulong first = (1ul << 60) + 3;
        Assert.Equal(.000017, clock.Milliseconds(first, first + 17), 12);
        Assert.Equal(0, clock.Milliseconds(first, first));
        Assert.Throws<InvalidOperationException>(() => clock.Milliseconds(first, first - 1));
        Assert.Throws<InvalidOperationException>(() => default(GpuTimestampClock).Milliseconds(0, 1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void InvalidPeriodsAreRejected(double period) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new GpuTimestampClock(period));
}
