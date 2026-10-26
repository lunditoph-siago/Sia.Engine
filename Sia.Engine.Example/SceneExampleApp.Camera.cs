using Sia;
using Sia.Engine;
using Sia.Engine.Rendering.Pbr;
using CameraComponent = Sia.Engine.Camera.Camera;

namespace Sia.Engine.Example;

internal sealed partial class SceneExampleApp
{
    private readonly PbrSceneCamera? _authoredCamera;

    private static PbrSceneCamera? SelectAuthoredCamera(Guid? id, PbrSceneData? scene)
    {
        if (id is null) return null;
        if (scene is not null)
            foreach (var camera in scene.Cameras)
                if (camera.Id == id.Value) return camera;
        throw new ArgumentException("The selected camera UUID is absent from the scene.", nameof(id));
    }

    private bool ApplyAuthoredCamera()
    {
        if (_authoredCamera is not { } source) return false;
        var aspect = (float)System.Math.Max(1, _framebufferWidth) / System.Math.Max(1, _framebufferHeight);
        _camera.Get<CameraComponent>() = new(source.GetVerticalFov(aspect), source.Near, source.Far);
        _sceneWorld!.Execute(_camera, new Transform.SetPosition(source.Position));
        _sceneWorld.Execute(_camera, new Transform.SetRotation(source.Rotation));
        return true;
    }
}
