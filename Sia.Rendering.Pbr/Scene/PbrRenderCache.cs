using Sia.Math;

namespace Sia.Engine.Rendering.Pbr;

public sealed class PbrRenderCache : MeshRenderCache<PbrMaterial, PbrRenderInstance>
{
    protected override PbrRenderInstance Convert(in MeshSceneInstance<PbrMaterial> instance)
    {
        var material = instance.Material;
        return new(instance.Transform, instance.Normal, new float4(material.BaseColor, 1),
            new float4(material.Metallic, material.Roughness, 0, 0),
            new float4(material.EmissiveColor, material.EmissiveStrength));
    }
}
