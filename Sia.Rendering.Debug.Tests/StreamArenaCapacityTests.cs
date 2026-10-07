using Sia.Engine.Rendering.Pbr;
using Xunit;

namespace Sia.Rendering.Debug.Tests;

public class StreamArenaCapacityTests
{
    [Theory]
    [InlineData(36u)]
    [InlineData(40u)]
    [InlineData(48u)]
    public void IndexedDetailGetsCapacityForItsActualShape(uint vertexBytes)
    {
        // A shared grid has roughly two triangles per vertex; a fixed80/20 byte split starves its indices.
        var bytes = 1000ul * vertexBytes + 2000ul * 12;
        var capacity = PbrGpuScene.StreamDetailCapacity(bytes / 2, 1000, 2000, vertexBytes);
        Assert.Equal(500ul, capacity.Vertices);
        Assert.Equal(1000ul, capacity.Triangles);
        Assert.Equal(bytes / 2, capacity.Vertices * vertexBytes + capacity.Triangles * 12);
    }

    [Fact]
    public void SmallOrEmptyDetailDoesNotReserveUnusedCapacity()
    {
        Assert.Equal((3ul, 1ul), PbrGpuScene.StreamDetailCapacity(64ul * 1024 * 1024, 3, 1, 40));
        Assert.Equal((0ul, 0ul), PbrGpuScene.StreamDetailCapacity(1024, 0, 0, 40));
        var small = PbrGpuScene.StreamDetailCapacity(131, 3, 1, 40);
        Assert.True(small.Vertices * 40 + small.Triangles * 12 <= 131);
    }

    [Fact]
    public void LargeValidShapeDoesNotOverflowDuringProportionalAllocation()
    {
        var capacity = PbrGpuScene.StreamDetailCapacity(1ul << 40, 1ul << 40, 1ul << 40, 40);
        Assert.True(capacity.Vertices * 40 + capacity.Triangles * 12 <= 1ul << 40);
        Assert.Equal(capacity.Vertices, capacity.Triangles);
    }
}
