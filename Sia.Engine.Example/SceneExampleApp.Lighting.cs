using Sia.Engine.Rendering;
using Sia.Engine.Rendering.Pbr;

namespace Sia.Engine.Example;

internal sealed partial class SceneExampleApp
{
    private LightingFiles.Prepared? _preparedLighting;
    private IblEnvironmentAsset? _bakedEnvironment;
    private DiffuseProbeAsset? _bakedProbes;
    private LightmapFiles.Prepared? _preparedLightmaps;
    private PbrLightmapInput? _lightmapInput;

    private void AcquireLightmapAssets(PbrSceneData scene)
    {
        if (_preparedLightmaps is not { } prepared) return;
        _lightmapInput = LightmapFiles.CreateInput(_renderWorld!.Entities, prepared, scene, _assetRoot);
        _preparedLightmaps = null;
    }

    private void AcquireLightingAssets()
    {
        if (_preparedLighting is not { } prepared) return;
        if (prepared.Environment is null && prepared.Probes is null) { _preparedLighting = null; return; }
        if (!_assetRoot.IsValid) _assetRoot = RenderAssetUse.Create(_renderWorld!.Entities);
        var acquired = LightingFiles.Acquire(_renderWorld!.Entities, prepared, _assetRoot);
        _bakedEnvironment = acquired.Environment.IsValid ? acquired.Environment.Get<IblEnvironmentResource>().Data : null;
        _bakedProbes = acquired.Probes.IsValid ? acquired.Probes.Get<DiffuseProbeResource>().Data : null;
        _preparedLighting = null;
    }
}
