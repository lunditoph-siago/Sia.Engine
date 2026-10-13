using Sia.Engine.Rendering.Pbr;

namespace Sia.Engine.Example;

internal sealed partial class SceneExampleApp
{
    private PbrMeshInstance? _movingInstance;
    private float _instancePhase;

    private void UpdateInstanceMotion(float deltaTime)
    {
        if (_movingInstance is not { } instance || _sceneRenderer is null) return;
        _instancePhase = (_instancePhase + deltaTime) % MathF.Tau;
        var transform = instance.Transform; transform.c3.y += MathF.Sin(_instancePhase) * .1f;
        _sceneRenderer.UpdateInstanceTransforms([new(instance.Id, transform)]);
    }
}
