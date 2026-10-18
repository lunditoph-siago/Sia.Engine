using Sia.Asset;
using Sia.Engine.Mesh;

namespace Sia.Engine.Rendering.Pbr;

/// <summary>Validated borrowed render inputs. The caller retains original assets until renderer disposal.</summary>
public sealed class PbrReflectionCaptureInput
{
    private readonly byte[] _renderIdentity;
    public PbrReflectionCaptureData Capture { get; }
    public IblEnvironmentAsset Environment { get; }
    public ReadOnlyMemory<byte> RenderIdentity => _renderIdentity;

    private PbrReflectionCaptureInput(PbrReflectionCaptureData capture, IblEnvironmentAsset environment, byte[] identity)
        => (Capture, Environment, _renderIdentity) = (capture, environment, identity);

    /// <summary>Pure validation over acquired values, without geometry loading, IO, asset mutation or GPU effects.</summary>
    public static PbrReflectionCaptureInput Create(PbrReflectionCaptureData capture, PbrSceneData scene,
        Func<AssetRefer<MeshManifestRecord>.Id, MeshManifest> meshes,
        Func<AssetRefer<PbrMaterialRecord>.Id, PbrMaterialData> materials,
        Func<AssetRefer<PbrTextureRecord>.Id, PbrTextureData> textures,
        IblEnvironmentAsset source, IblEnvironmentAsset environment, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(capture); ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(environment);
        capture.ValidateScene(scene, meshes, materials, textures, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (source.Write(Stream.Null) != capture.SourceEnvironment.ContentHash || environment.Write(Stream.Null) != capture.Environment.ContentHash
            || source.Sky != environment.Sky || !source.Coefficients.Span.SequenceEqual(environment.Coefficients.Span)
            || !source.BrdfLut.Span.SequenceEqual(environment.BrdfLut.Span))
            throw new InvalidAssetException("Capture source/output content or shared sky/SH/BRDF differs from its dependencies.");
        var values = new Dictionary<AssetId, PbrMaterialAsset>();
        PbrMaterialAsset Material(PbrMaterialSlot slot)
        {
            if (!values.TryGetValue(slot.Material.Value, out var value)) {
                value = materials(slot.Material).ResolveTextures(textures); values.Add(slot.Material.Value, value);
            }
            return value;
        }
        var identity = PbrSceneTransport.ReferenceIdentity(scene, binding => meshes(binding.Mesh), Material, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested(); return new(capture, environment, identity);
    }
}
