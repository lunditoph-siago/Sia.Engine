namespace Sia.Engine.Rendering.Pbr;

/// <summary>One transient mixed render input with a common authored transport identity. Borrows acquired products.</summary>
public sealed class PbrMixedScene
{
    /// <summary>Whole Raster input; empty when <see cref="RasterPages"/> supplies the conventional subset.</summary>
    public PbrRasterScene Raster { get; }
    /// <summary>Optional exact paged Raster input. Existing whole-Raster compositions leave this null.</summary>
    public PbrRasterPagesScene? RasterPages { get; }
    public PbrVirtualScene Virtual { get; }
    public ReadOnlyMemory<byte> TransportIdentity { get; }
    internal PbrMixedScene(PbrRasterScene raster, PbrVirtualScene virtualScene, byte[] identity)
        => (Raster, Virtual, TransportIdentity) = (raster, virtualScene, identity);

    internal PbrMixedScene(PbrRasterPagesScene raster, PbrVirtualScene virtualScene, byte[] identity)
    {
        Raster = PbrRasterScene.Create([], [], []);
        (RasterPages, Virtual, TransportIdentity) = (raster, virtualScene, identity);
    }
}
