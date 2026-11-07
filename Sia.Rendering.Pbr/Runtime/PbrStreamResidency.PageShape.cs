namespace Sia.Engine.Rendering.Pbr;

internal sealed partial class PbrStreamResidency
{
    // Admission retains the authenticated descriptor's conservative Raster
    // bound. Physical packing must instead preserve the acquired page's exact
    // shape through planning, reservation, reread and installation.
    private readonly record struct PageShape(uint Vertices, uint Triangles, bool Raster)
    {
        public static PageShape From(StreamGeometryPage page)
            => new(checked((uint)page.VertexCount), checked((uint)page.TriangleCount), page.Data is null);

        public bool Matches(StreamPageAllocation allocation)
            => Vertices == allocation.Vertices.Count && Triangles == allocation.Triangles.Count;
    }

    private static bool ValidPageShape(in PbrSceneStream.PageInfo declared, bool raster, PageShape actual)
        => actual.Raster == raster && declared.Vertices > 0 && declared.Triangles > 0
            && actual.Vertices > 0 && actual.Triangles == (uint)declared.Triangles
            && (raster ? actual.Vertices <= (uint)declared.Vertices : actual.Vertices == (uint)declared.Vertices);

    private bool ValidPageShape(Key key, PageShape shape)
        => RecoveryKeyOwned(key) && ValidPageShape(_pages[key.Page], _source.IsRasterPage(_pages[key.Page].Id), shape);

    private PageShape AcquiredPageShape(Key key, StreamGeometryPage page)
    {
        var shape = PageShape.From(page);
        if (!ValidPageShape(key, shape)) throw new InvalidDataException("Acquired geometry shape exceeds or disagrees with its typed page descriptor.");
        return shape;
    }
}
