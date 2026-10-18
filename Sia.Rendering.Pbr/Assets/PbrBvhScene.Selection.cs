using System.Runtime.CompilerServices;

namespace Sia.Engine.Rendering.Pbr;

public sealed partial class PbrBvhScene
{
    /// <summary>
    /// Selects an explicitly authored static domain in canonical Guid order. Borrows already acquired
    /// geometry, material values and bindings; performs no acquisition, geometry rebuild or IO.
    /// The caller retains all original assets. The resulting identity uses the exact bake contract.
    /// </summary>
    public PbrBvhScene SelectInstances(ReadOnlySpan<Guid> instanceIds, ulong maximumBytes = 128ul * 1024 * 1024,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (instanceIds.Length > _instances.Length) throw new ArgumentException("Too many selected instances.", nameof(instanceIds));
        // Owned instance/identity values; excludes borrowed assets, object headers and bounded validation scratch.
        var bytes = (ulong)instanceIds.Length * (ulong)Unsafe.SizeOf<PbrMeshInstance>() + 32;
        if (bytes > maximumBytes) throw new ArgumentException("Selected static-domain metadata exceeds its budget.", nameof(maximumBytes));
        var requested = new HashSet<Guid>();
        foreach (var id in instanceIds) {
            cancellationToken.ThrowIfCancellationRequested();
            if (id == Guid.Empty || !requested.Add(id)) throw new ArgumentException("Selected IDs must be valid and unique.", nameof(instanceIds));
        }
        var selected = new PbrMeshInstance[requested.Count]; var count = 0;
        foreach (var instance in _instances) {
            cancellationToken.ThrowIfCancellationRequested();
            if (requested.Contains(instance.Id)) selected[count++] = instance;
        }
        if (count != selected.Length) throw new ArgumentException("Selected instance is absent from the acquired scene.", nameof(instanceIds));
        Array.Sort(selected, static (a, b) => a.Id.CompareTo(b.Id));
        var identity = PbrSceneTransport.ReferenceIdentity(selected, at => _bindings[at].TransportRevision, cancellationToken);
        return new(_geometry, _materials, _materialIds, _bindings, selected, Attribution, identity, cancellationToken);
    }
}
