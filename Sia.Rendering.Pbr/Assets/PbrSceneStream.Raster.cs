using Sia.Engine.Mesh;

namespace Sia.Engine.Rendering.Pbr;

public sealed partial class PbrSceneStream
{
    internal PbrRasterScene? Raster { get; }

    /// <summary>Streams independent texture mips while retaining conventional Raster geometry and LODs.</summary>
    public static PbrSceneStream Create(PbrRasterScene scene,
        Func<PbrTextureData, PbrTextureMipsData?> textureMips,
        Func<PbrTextureMipReference, CancellationToken, Task<PbrTextureRecord>> prepareMip)
    {
        ArgumentNullException.ThrowIfNull(scene); ArgumentNullException.ThrowIfNull(textureMips); ArgumentNullException.ThrowIfNull(prepareMip);
        return new(scene, textureMips, prepareMip);
    }

    private PbrSceneStream(PbrRasterScene scene, Func<PbrTextureData, PbrTextureMipsData?> textureMips,
        Func<PbrTextureMipReference, CancellationToken, Task<PbrTextureRecord>> prepareMip)
    {
        Raster = scene; (_findTextureMips, _prepareMip) = (textureMips, prepareMip);
        _prepare = static (_, _) => throw new InvalidOperationException("Conventional geometry has no virtual page requests.");
        _acquire = static (_, _) => throw new InvalidOperationException("Conventional geometry has no virtual page acquisition.");
        Bounds = GeometryBounds = scene.Bounds; Identity = scene.TransportIdentity;
        SourceInstanceCount = scene.Instances.Length; Materials = InitializeTextures(scene.Materials.Span);
        Instances = ReadOnlyMemory<VisibilityInstance>.Empty; Hierarchies = []; TransparentGeometry = []; TransparentInstances = [];
    }
}
