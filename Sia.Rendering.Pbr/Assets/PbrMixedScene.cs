namespace Sia.Engine.Rendering.Pbr;

/// <summary>One transient mixed render input with a common authored transport identity. Borrows acquired products.</summary>
public sealed class PbrMixedScene
{
    public PbrRasterScene Raster { get; }
    public PbrVirtualScene Virtual { get; }
    public ReadOnlyMemory<byte> TransportIdentity { get; }
    internal PbrMixedScene(PbrRasterScene raster, PbrVirtualScene virtualScene, byte[] identity)
        => (Raster, Virtual, TransportIdentity) = (raster, virtualScene, identity);
}
