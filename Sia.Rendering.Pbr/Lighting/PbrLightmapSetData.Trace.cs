namespace Sia.Engine.Rendering.Pbr;

public sealed partial class PbrLightmapSetData
{
    /// <summary>
    /// Selects the frozen bake domain from already acquired independent BVHs and verifies every receiver
    /// and the exact static transport identity. No lighting members, source products or assets are loaded.
    /// The caller retains acquired geometry/materials for the lifetime of the returned reference scene.
    /// </summary>
    public PbrBvhScene SelectReference(PbrBvhScene scene, ulong maximumBytes = 128ul * 1024 * 1024,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scene); cancellationToken.ThrowIfCancellationRequested();
        var ids = new HashSet<Guid>();
        foreach (var entry in _receivers) { cancellationToken.ThrowIfCancellationRequested(); ids.Add(entry.Receiver.InstanceId); }
        var selected = scene.SelectInstances(ids.ToArray(), maximumBytes, cancellationToken);
        if (Convert.ToHexString(selected.TransportIdentity.Span) != SceneIdentity)
            throw new InvalidDataException("Static reference source, material or transform domain differs from the bake.");
        var instances = new Dictionary<Guid, int>(selected.Instances.Length);
        for (var at = 0; at < selected.Instances.Length; at++) instances.Add(selected.Instances[at].Id, at);
        foreach (var entry in _receivers) {
            cancellationToken.ThrowIfCancellationRequested(); var receiver = entry.Receiver;
            var binding = selected.Bindings[selected.Instances[instances[receiver.InstanceId]].Binding];
            if (binding.SourceHash != receiver.SourceHash)
                throw new InvalidDataException("Static receiver source revision differs from its acquired BVH inventory.");
            _ = binding.GetMaterial(receiver.MaterialSlot);
        }
        return selected;
    }
}
