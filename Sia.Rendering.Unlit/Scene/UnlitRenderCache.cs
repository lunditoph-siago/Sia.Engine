namespace Sia.Engine.Rendering.Unlit;

internal sealed class UnlitRenderCache : MeshRenderCache<UnlitMaterial, UnlitInstance>
{
    protected override UnlitInstance Convert(in MeshSceneInstance<UnlitMaterial> instance)
        => new(instance.Transform, instance.Normal, instance.Material.Color);
}
