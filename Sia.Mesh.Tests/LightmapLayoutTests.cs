using Sia.Engine.Mesh;
using Sia.Math;
using Xunit;

namespace Sia.Mesh.Tests;

public sealed class LightmapLayoutTests
{
    private static MeshData Grid(int segments)
    {
        var vertices = new MeshVertex[(segments + 1) * (segments + 1)];
        var indices = new List<uint>();
        for (var y = 0; y <= segments; y++)
            for (var x = 0; x <= segments; x++) {
                var p = new float3((float)x / segments, (float)y / segments, 0);
                vertices[y * (segments + 1) + x] = new(p, new(0, 0, 1), new(p.x * 5, p.y * 7)) {
                    Tangent = new(1, 0, 0, 1)
                };
                if (x == segments || y == segments) continue;
                var a = (uint)(y * (segments + 1) + x);
                var b = a + 1;
                var c = a + (uint)segments + 1;
                indices.AddRange([a, b, c + 1, a, c + 1, c]);
            }
        return new(vertices, indices.ToArray(), new(float3.zero, new(1, 1, 0)));
    }

    private static double Area(float2 a, float2 b, float2 c)
        => ((double)b.x - a.x) * ((double)c.y - a.y) - ((double)b.y - a.y) * ((double)c.x - a.x);

    private static void CheckLayout(MeshData original, MeshLightmapLayout layout)
    {
        Assert.Equal(original.Indices.Length / 3, layout.TriangleCharts.Length);
        Assert.Equal(original.Bounds, layout.Mesh.Bounds);
        Assert.Equal(original.Indices.Length, layout.Mesh.Indices.Length);
        for (var t = 0; t < original.Indices.Length / 3; t++) {
            var rect = layout.Charts.Span[layout.TriangleCharts.Span[t]];
            for (var c = 0; c < 3; c++) {
                var input = original.Vertices[original.Indices[t * 3 + c]];
                var output = layout.Mesh.Vertices[layout.Mesh.Indices[t * 3 + c]];
                Assert.Equal(input, output with { LightmapUV = input.LightmapUV });
                var x = (double)output.LightmapUV.x * layout.Resolution;
                var y = (double)output.LightmapUV.y * layout.Resolution;
                Assert.InRange(x, rect.X + layout.Padding + .499, rect.X + rect.Width - layout.Padding - .499);
                Assert.InRange(y, rect.Y + layout.Padding + .499, rect.Y + rect.Height - layout.Padding - .499);
            }
            var a = layout.Mesh.Vertices[layout.Mesh.Indices[t * 3]].LightmapUV;
            var b = layout.Mesh.Vertices[layout.Mesh.Indices[t * 3 + 1]].LightmapUV;
            var cUv = layout.Mesh.Vertices[layout.Mesh.Indices[t * 3 + 2]].LightmapUV;
            Assert.True(Area(a, b, cUv) > 0);
        }
        for (var i = 0; i < layout.Charts.Length; i++) {
            var a = layout.Charts.Span[i];
            Assert.InRange(a.X, 0, layout.Resolution - a.Width);
            Assert.InRange(a.Y, 0, layout.Resolution - a.Height);
            for (var j = 0; j < i; j++) {
                var b = layout.Charts.Span[j];
                Assert.True(a.X + a.Width <= b.X || b.X + b.Width <= a.X
                    || a.Y + a.Height <= b.Y || b.Y + b.Height <= a.Y);
            }
        }
    }

    [Fact]
    public void PlanarGridUsesOneDeterministicChartAndCanStillSimplify()
    {
        var mesh = Grid(16);
        var layout = MeshLightmap.Generate(mesh, 64, 2);
        CheckLayout(mesh, layout);
        Assert.Single(layout.Charts.ToArray());
        Assert.Equal(mesh.Vertices.Length, layout.Mesh.Vertices.Length);
        Assert.All(layout.TriangleCharts.ToArray(), id => Assert.Equal(0, id));
        var again = MeshLightmap.Generate(mesh, 64, 2);
        Assert.Equal(layout.Mesh.Vertices, again.Mesh.Vertices);
        Assert.Equal(layout.Mesh.Indices, again.Mesh.Indices);
        Assert.Equal(layout.Charts.ToArray(), again.Charts.ToArray());
        Assert.Equal(layout.TriangleCharts.ToArray(), again.TriangleCharts.ToArray());
        var asset = MeshPatchAsset.Cook(layout.Mesh, new(8, 4, .5f, 1, 1));
        Assert.True(asset.HasLightmapUV);
        Assert.True(asset.Build.SimplificationCount > 0);
        Assert.True(asset.ExtractRoots().Build.Tree.FinestTriangleCount < mesh.Indices.Length / 3);
        Assert.True(MeshPatchAsset.Decode(asset.EncodeCompressed()).Build.Tree.Vertices.SequenceEqual(asset.Build.Tree.Vertices));
    }

    [Fact]
    public void HardEdgeDuplicatesSharedVerticesAndNegativeFacingChartsStayOriented()
    {
        MeshVertex[] vertices = [new(new(0, 0, 0), new(0, 0, 1), new(0, 0)),
            new(new(1, 0, 0), new(0, 0, 1), new(1, 0)), new(new(0, 1, 0), new(0, 0, 1), new(0, 1)),
            new(new(0, 0, 1), new(0, -1, 0), new(1, 1))];
        var mesh = new MeshData(vertices, [0, 1, 2, 1, 0, 3], new(float3.zero, new(1, 1, 1)));
        var layout = MeshLightmap.Generate(mesh, 32);
        CheckLayout(mesh, layout);
        Assert.Equal(2, layout.Charts.Length);
        Assert.Equal(6, layout.Mesh.Vertices.Length);
        Assert.NotEqual(layout.Mesh.Vertices[layout.Mesh.Indices[0]].LightmapUV,
            layout.Mesh.Vertices[layout.Mesh.Indices[4]].LightmapUV);
    }

    [Fact]
    public void DisconnectedOverlappingSurfacesGetDifferentAllocations()
    {
        var grid = Grid(1);
        var vertices = grid.Vertices.Concat(grid.Vertices.Select(v => v with { Position = v.Position + new float3(0, 0, .01f) })).ToArray();
        var mesh = new MeshData(vertices, grid.Indices.Concat(grid.Indices.Select(i => i + 4)).ToArray(),
            new(float3.zero, new(1, 1, .01f)));
        var layout = MeshLightmap.Generate(mesh, 32, 3);
        CheckLayout(mesh, layout);
        Assert.Equal(2, layout.Charts.Length);
        Assert.NotEqual(layout.TriangleCharts.Span[0], layout.TriangleCharts.Span[2]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AStripThatWrapsOverItsParameterizationSplitsBeforePacking(bool curved)
    {
        // Five connected, consistently wound fan triangles; the last overlays the first.
        float3[] points = [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0), new(-1, 0, 0),
            new(0, -1, 0), new(1, 0, 0), new(0, 1, 0)];
        var mesh = new MeshData(points.Select((p, i) => new MeshVertex(
                p + new float3(0, 0, curved ? i % 3 * .2f : 0), new(0, 0, 1), curved ? new(p.x, p.y) : default)).ToArray(),
            [0, 1, 2, 0, 2, 3, 0, 3, 4, 0, 4, 5, 0, 5, 6], new(new(-1, -1, 0), new(1, 1, 0)));
        var layout = MeshLightmap.Generate(mesh, 64);
        CheckLayout(mesh, layout);
        Assert.Equal(5, layout.Charts.Length);
        Assert.Equal(15, layout.Mesh.Vertices.Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CurvedStripReusesAValidatedSourceParameterizationIncludingMirroring(bool mirrored)
    {
        var grid = Grid(16);
        var vertices = grid.Vertices.Select(v => {
            var angle = v.Position.x * 2;
            return v with { Position = new(MathF.Cos(angle), MathF.Sin(angle), v.Position.y),
                Normal = new(MathF.Cos(angle), MathF.Sin(angle), 0),
                UV = new(mirrored ? -v.UV.x : v.UV.x, v.UV.y) };
        }).ToArray();
        var mesh = grid with { Vertices = vertices, Bounds = new(new(-1, -1, 0), new(1, 1, 1)) };
        var layout = MeshLightmap.Generate(mesh, 64);
        CheckLayout(mesh, layout);
        Assert.Single(layout.Charts.ToArray());
        Assert.Equal(mesh.Vertices.Length, layout.Mesh.Vertices.Length);
    }

    [Fact]
    public void PositionAndUvMatchingAcrossShadingSeamsUsesOneChartWithoutWeldingVertices()
    {
        var grid = Grid(4);
        var vertices = grid.Indices.Select((index, corner) => grid.Vertices[index] with {
            Normal = math.normalize(new float3(corner % 3 * .2f, 0, 1)),
            Tangent = new(0, 1, 0, corner % 2 == 0 ? 1 : -1)
        }).ToArray();
        var soup = new MeshData(vertices, Enumerable.Range(0, vertices.Length).Select(i => (uint)i).ToArray(), grid.Bounds);
        var layout = MeshLightmap.Generate(soup, 32);
        CheckLayout(soup, layout);
        Assert.Single(layout.Charts.ToArray());
        Assert.Equal(vertices.Length, layout.Mesh.Vertices.Length);
        for (var corner = 0; corner < grid.Indices.Length; corner++) {
            var matching = Array.FindIndex(grid.Indices, index => index == grid.Indices[corner]);
            Assert.Equal(layout.Mesh.Vertices[layout.Mesh.Indices[matching]].LightmapUV,
                layout.Mesh.Vertices[layout.Mesh.Indices[corner]].LightmapUV);
        }
        Assert.Equal(layout.Mesh.Vertices, MeshLightmap.Generate(soup, 32).Mesh.Vertices);
    }

    [Fact]
    public void UvDiscontinuitiesNearbySurfacesAndAmbiguousCoincidentEdgesStaySplit()
    {
        var grid = Grid(1);
        var vertices = grid.Indices.Select(i => grid.Vertices[i]).ToArray();
        var indices = Enumerable.Range(0, vertices.Length).Select(i => (uint)i).ToArray();
        var uvSeam = vertices.ToArray();
        for (var i = 3; i < 6; i++) uvSeam[i] = uvSeam[i] with { UV = uvSeam[i].UV + new float2(10, 0) };
        var discontinuous = new MeshData(uvSeam, indices, grid.Bounds);
        var uvLayout = MeshLightmap.Generate(discontinuous, 32);
        CheckLayout(discontinuous, uvLayout);
        Assert.Equal(2, uvLayout.Charts.Length);
        var near = vertices.ToArray();
        for (var i = 3; i < 6; i++) near[i] = near[i] with { Position = near[i].Position + new float3(0, 0, 1e-7f) };
        Assert.Equal(2, MeshLightmap.Generate(new(near, indices, grid.Bounds), 32).Charts.Length);
        var duplicate = new MeshData(vertices.Concat(vertices).ToArray(), indices.Concat(indices.Select(i => i + 6)).ToArray(), grid.Bounds);
        var ambiguous = MeshLightmap.Generate(duplicate, 32);
        CheckLayout(duplicate, ambiguous);
        Assert.Equal(4, ambiguous.Charts.Length);
    }

    [Fact]
    public void ThinRegionsKeepAUsablePixelIntervalWithoutExpandingTheLongAxis()
    {
        var quad = Grid(1);
        var mesh = quad with { Vertices = quad.Vertices.Select(v => v with {
            Position = new(v.Position.x * 1000, v.Position.y * .001f, 0)
        }).ToArray(), Bounds = new(float3.zero, new(1000, .001f, 0)) };
        var layout = MeshLightmap.Generate(mesh, 16);
        CheckLayout(mesh, layout);
        Assert.Single(layout.Charts.ToArray());
        var coordinates = layout.Mesh.Vertices.Select(v => v.LightmapUV).ToArray();
        Assert.InRange(coordinates.Max(uv => uv.x) - coordinates.Min(uv => uv.x), .68f, .69f);
        Assert.Equal(1f / 16, coordinates.Max(uv => uv.y) - coordinates.Min(uv => uv.y), 6);
    }

    [Fact]
    public void PackedFloatPrecisionReprojectsOnlyCollapsedTriangles()
    {
        var grid = Grid(1);
        var vertices = grid.Vertices.Select(v => v with { Position = v.Position * 1000 }).Concat(new MeshVertex[] {
            new(new(0, 0, 2), new(0, 0, 1), default), new(new(1, 1, 2), new(0, 0, 1), default),
            new(new(1, MathF.BitIncrement(1), 2), new(0, 0, 1), default) }).ToArray();
        var mesh = new MeshData(vertices, [.. grid.Indices, 4, 5, 6], new(float3.zero, new(1000, 1000, 2)));
        var layout = MeshLightmap.Generate(mesh, 16);
        CheckLayout(mesh, layout);
        Assert.Equal(2, layout.Charts.Length);
        var again = MeshLightmap.Generate(mesh, 16);
        Assert.Equal(layout.Mesh.Vertices, again.Mesh.Vertices);
        Assert.Equal(layout.Mesh.Indices, again.Mesh.Indices);
    }

    [Fact]
    public void InvalidTopologyValuesCapacityAndCancellationFailExplicitly()
    {
        var grid = Grid(1);
        Assert.Throws<ArgumentException>(() => MeshLightmap.Generate(grid with { Indices = [0, 1] }));
        Assert.Throws<ArgumentException>(() => MeshLightmap.Generate(grid with { Indices = [0, 1, 99] }));
        Assert.Throws<ArgumentException>(() => MeshLightmap.Generate(grid with { Indices = [0, 0, 1] }));
        Assert.Throws<ArgumentException>(() => MeshLightmap.Generate(grid with { Indices = [0, 1, 2, 0, 1, 3] }));
        var bad = grid.Vertices.ToArray();
        bad[0] = bad[0] with { Position = new(float.NaN, 0, 0) };
        Assert.Throws<ArgumentException>(() => MeshLightmap.Generate(grid with { Vertices = bad }));
        Assert.Throws<ArgumentOutOfRangeException>(() => MeshLightmap.Generate(grid, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => MeshLightmap.Generate(grid, 8, 4));
        var soup = new MeshData(grid.Indices.Select(i => grid.Vertices[i]).ToArray(), [0, 1, 2, 3, 4, 5], grid.Bounds);
        Assert.Single(MeshLightmap.Generate(soup, 8, 2).Charts.ToArray());
        var disconnected = soup.Vertices.ToArray();
        for (var i = 3; i < 6; i++) disconnected[i] = disconnected[i] with { Position = disconnected[i].Position + new float3(0, 0, .1f) };
        Assert.Throws<InvalidOperationException>(() => MeshLightmap.Generate(soup with { Vertices = disconnected }, 8, 2));
        Assert.Throws<OperationCanceledException>(() => MeshLightmap.Generate(grid, cancellationToken: new(true)));
        Assert.All(grid.Vertices, v => Assert.Equal(float2.zero, v.LightmapUV));
    }
}
