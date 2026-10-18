using Sia.Asset;
using Sia.Engine.Mesh;
using MeshReference=Sia.Asset.AssetRefer<Sia.Engine.Mesh.MeshManifestRecord>.Id;
using MaterialReference=Sia.Asset.AssetRefer<Sia.Engine.Rendering.Pbr.PbrMaterialRecord>.Id;
using TextureReference=Sia.Asset.AssetRefer<Sia.Engine.Rendering.Pbr.PbrTextureRecord>.Id;

namespace Sia.Engine.Rendering.Pbr;

public sealed partial class PbrLightmapSetData
{
    /// <summary>Checks the static domain without reading any referenced lighting product.</summary>
    public void ValidateScene(PbrSceneData scene,Func<MeshReference,MeshManifest> meshes,
        Func<MaterialReference,PbrMaterialData> materials,Func<TextureReference,PbrTextureData> textures,
        CancellationToken cancellationToken=default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var receivers=new PbrLightmapReceiver[_receivers.Length];
        for (var at=0;at<receivers.Length;at++) receivers[at]=_receivers[at].Receiver;
        PbrLightmapSceneValidation.Validate(SceneIdentity,receivers,scene,meshes,materials,textures,cancellationToken);
    }

    /// <summary>Draw bindings use authored instance/slot identity, independently of product loading.</summary>
    public int[] BindReceivers(ReadOnlySpan<PbrSceneInstance> instances,CancellationToken cancellationToken=default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (instances.Length>PbrSceneData.MaximumInstances) throw new ArgumentException("Lightmap draws exceed the instance limit.",nameof(instances));
        var lookup=new Dictionary<(Guid,int),int>(_receivers.Length);
        for (var at=0;at<_receivers.Length;at++) {
            var receiver=_receivers[at].Receiver; lookup.Add((receiver.InstanceId,receiver.MaterialSlot),at);
        }
        var result=new int[instances.Length]; var covered=new bool[_receivers.Length]; var count=0;
        for (var at=0;at<instances.Length;at++) {
            cancellationToken.ThrowIfCancellationRequested(); var instance=instances[at];
            if (instance.AuthoredId==Guid.Empty || instance.MaterialSlot<0) throw new ArgumentException("Lightmap draws require authored identity and material slot.");
            if (!lookup.TryGetValue((instance.AuthoredId,instance.MaterialSlot),out var receiver)) { result[at]=-1; continue; }
            result[at]=receiver; if (!covered[receiver]) { covered[receiver]=true; count++; }
        }
        if (count!=_receivers.Length) throw new ArgumentException("Render input omits a static lightmap receiver.",nameof(instances));
        return result;
    }

    internal int[] BindReceivers(PbrSceneStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream); var opaque=stream.Instances.Span; var transparent=stream.TransparentInstances;
        if (opaque.Length+(long)transparent.Length>PbrSceneData.MaximumInstances) throw new ArgumentException("Too many lighting draws.");
        var lookup=new Dictionary<(Guid,int),int>(_receivers.Length);
        for (var at=0;at<_receivers.Length;at++) lookup.Add((_receivers[at].Receiver.InstanceId,_receivers[at].Receiver.MaterialSlot),at);
        var result=new int[opaque.Length+transparent.Length]; var covered=new bool[_receivers.Length]; var count=0;
        int Bind(Guid id,int slot) {
            if (id==Guid.Empty || slot<0) throw new ArgumentException("Lighting draws require authored identity and material slot.");
            if (!lookup.TryGetValue((id,slot),out var receiver)) return -1;
            if (!covered[receiver]) { covered[receiver]=true; count++; } return receiver;
        }
        for (var at=0;at<opaque.Length;at++) result[at]=Bind(opaque[at].AuthoredId,opaque[at].MaterialSlot);
        for (var at=0;at<transparent.Length;at++) result[opaque.Length+at]=Bind(transparent[at].AuthoredId,transparent[at].MaterialSlot);
        if (count!=_receivers.Length) throw new ArgumentException("Render input omits a static lighting receiver.");
        return result;
    }
}
