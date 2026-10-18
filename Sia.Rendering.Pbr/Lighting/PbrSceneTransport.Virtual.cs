namespace Sia.Engine.Rendering.Pbr;

public static partial class PbrSceneTransport
{
    public static byte[] Identity(PbrVirtualScene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        return scene.TransportIdentity.ToArray();
    }
}
