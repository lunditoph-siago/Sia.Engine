using Sia.Engine.Mesh;
using Sia.Math;
using Xunit;

namespace Sia.Mesh.Tests;

public sealed class ProceduralMeshTests
{
    [Fact]
    public void CylinderProducesClosedMeshWithExpectedBounds()
    {
        var mesh = ProceduralMesh.Cylinder(radius: 2.0f, height: 6.0f, radialSegments: 8);

        Assert.Equal(38, mesh.Vertices.Length);
        Assert.Equal(96, mesh.Indices.Length);
        AssertBounds(mesh, 2.0f, 3.0f);
        AssertValid(mesh);
    }

    [Fact]
    public void CapsuleProducesExpectedBounds()
    {
        var mesh = ProceduralMesh.Capsule(
            radius: 2.0f,
            halfLength: 3.0f,
            hemisphereSegments: 4,
            radialSegments: 8);

        Assert.Equal(90, mesh.Vertices.Length);
        Assert.Equal(432, mesh.Indices.Length);
        AssertBounds(mesh, 2.0f, 5.0f);
        AssertValid(mesh);
    }

    [Theory]
    [InlineData(0.0f, 1.0f, 8)]
    [InlineData(-1.0f, 1.0f, 8)]
    [InlineData(1.0f, 0.0f, 8)]
    [InlineData(1.0f, -1.0f, 8)]
    [InlineData(1.0f, 1.0f, 2)]
    public void CylinderRejectsInvalidArguments(float radius, float height, int radialSegments)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ProceduralMesh.Cylinder(radius, height, radialSegments));
    }

    [Theory]
    [InlineData(0.0f, 1.0f, 4, 8)]
    [InlineData(-1.0f, 1.0f, 4, 8)]
    [InlineData(1.0f, -1.0f, 4, 8)]
    [InlineData(1.0f, 1.0f, 0, 8)]
    [InlineData(1.0f, 1.0f, 4, 2)]
    public void CapsuleRejectsInvalidArguments(
        float radius,
        float halfLength,
        int hemisphereSegments,
        int radialSegments)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ProceduralMesh.Capsule(radius, halfLength, hemisphereSegments, radialSegments));
    }

    private static void AssertBounds(MeshData mesh, float radius, float halfHeight)
    {
        Assert.Equal(-radius, mesh.Bounds.Min.x, 5);
        Assert.Equal(-halfHeight, mesh.Bounds.Min.y, 5);
        Assert.Equal(-radius, mesh.Bounds.Min.z, 5);
        Assert.Equal(radius, mesh.Bounds.Max.x, 5);
        Assert.Equal(halfHeight, mesh.Bounds.Max.y, 5);
        Assert.Equal(radius, mesh.Bounds.Max.z, 5);
    }

    private static void AssertValid(MeshData mesh)
    {
        Assert.All(mesh.Vertices, static vertex => {
            Assert.True(float.IsFinite(vertex.Position.x));
            Assert.True(float.IsFinite(vertex.Position.y));
            Assert.True(float.IsFinite(vertex.Position.z));
            Assert.True(float.IsFinite(vertex.Normal.x));
            Assert.True(float.IsFinite(vertex.Normal.y));
            Assert.True(float.IsFinite(vertex.Normal.z));
        });
        Assert.All(mesh.Indices, index => Assert.True(index < mesh.Vertices.Length));

        for (var index = 0; index < mesh.Indices.Length; index += 3) {
            var first = mesh.Vertices[mesh.Indices[index]];
            var second = mesh.Vertices[mesh.Indices[index + 1]];
            var third = mesh.Vertices[mesh.Indices[index + 2]];
            var geometricNormal = math.cross(
                second.Position - first.Position,
                third.Position - first.Position);
            if (math.lengthsq(geometricNormal) <= 1e-10f) {
                continue;
            }

            var vertexNormal = first.Normal + second.Normal + third.Normal;
            Assert.True(math.dot(geometricNormal, vertexNormal) > 0.0f);
        }
    }
}
