using Sia.Engine.Rendering.Debug;
using Sia.Math;
using Xunit;

namespace Sia.Rendering.Debug.Tests;

public sealed class DebugDrawListTests
{
    private static readonly float4 Color = new(0.25f, 0.5f, 0.75f, 1.0f);

    [Fact]
    public void ClearRemovesGeneratedGeometry()
    {
        var list = new DebugDrawList();
        list.AddBox(RigidTransform.Identity, new float3(1.0f), Color);

        list.Clear();

        Assert.Equal(0, list.VertexCount);
        Assert.Equal(0, list.TriangleCount);
    }

    [Fact]
    public void BoxProducesTwelveFiniteTriangles()
    {
        var list = new DebugDrawList();

        list.AddBox(
            new RigidTransform(quaternion.RotateY(0.5f), new float3(4.0f, 2.0f, -3.0f)),
            new float3(1.0f, 2.0f, 3.0f),
            Color);

        Assert.Equal(12, list.TriangleCount);
        Assert.All(list.Vertices.ToArray(), static vertex => Assert.True(vertex.IsFinite));
    }

    [Fact]
    public void SphereProducesExpectedTriangleCount()
    {
        var list = new DebugDrawList();

        list.AddSphere(
            RigidTransform.Identity,
            radius: 2.0f,
            Color,
            longitudeSegments: 10,
            latitudeSegments: 6);

        Assert.Equal(100, list.TriangleCount);
    }

    [Fact]
    public void DegenerateLineProducesNoGeometry()
    {
        var list = new DebugDrawList();
        var position = new float3(1.0f, 2.0f, 3.0f);

        list.AddLine(position, position, 0.1f, Color);

        Assert.Equal(0, list.VertexCount);
    }

    [Fact]
    public void LineProducesTwoTrianglesPerSide()
    {
        var list = new DebugDrawList();

        list.AddLine(float3.zero, new float3(0.0f, 2.0f, 0.0f), 0.1f, Color, sideCount: 8);

        Assert.Equal(16, list.TriangleCount);
        Assert.All(list.Vertices.ToArray(), static vertex => Assert.True(vertex.IsFinite));
    }

    [Fact]
    public void CapsuleProducesFiniteJoinedHemispheres()
    {
        var list = new DebugDrawList();

        list.AddCapsule(
            RigidTransform.Identity,
            radius: 0.5f,
            halfLength: 1.0f,
            Color,
            sideCount: 8,
            hemisphereSegments: 3);

        Assert.Equal(96, list.TriangleCount);
        Assert.All(list.Vertices.ToArray(), static vertex => Assert.True(vertex.IsFinite));
    }
}
