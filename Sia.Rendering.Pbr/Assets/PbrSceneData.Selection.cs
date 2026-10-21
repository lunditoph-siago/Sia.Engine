namespace Sia.Engine.Rendering.Pbr;

public sealed partial class PbrSceneData
{
    /// <summary>
    /// Builds a reference-only scene of explicitly selected authored instances, canonically ordered by Guid.
    /// Unused bindings disappear; source records and the original scene remain unchanged.
    /// </summary>
    public PbrSceneData SelectInstances(ReadOnlySpan<Guid> instanceIds, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (instanceIds.Length > _instances.Length) throw new ArgumentException("Too many selected scene instances.", nameof(instanceIds));
        var requested = new HashSet<Guid>();
        var examined = 0;
        foreach (var id in instanceIds) {
            if ((examined++ & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (id == Guid.Empty || !requested.Add(id)) throw new ArgumentException("Selected instance identities must be valid and unique.", nameof(instanceIds));
        }
        var selected = new PbrMeshInstance[requested.Count]; var count = 0;
        examined = 0;
        foreach (var instance in _instances) {
            if ((examined++ & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (requested.Contains(instance.Id)) selected[count++] = instance;
        }
        if (count != selected.Length) throw new ArgumentException("A selected authored instance is absent from this scene.", nameof(instanceIds));
        Array.Sort(selected, static (a, b) => a.Id.CompareTo(b.Id));
        var remap = new int[_bindings.Length]; Array.Fill(remap, -1);
        foreach (var instance in selected) remap[instance.Binding] = 0;
        var bindings = new PbrMeshBinding[remap.Count(static index => index == 0)]; var next = 0;
        for (var binding = 0; binding < _bindings.Length; binding++)
            if (remap[binding] == 0) { remap[binding] = next; bindings[next++] = _bindings[binding]; }
        for (var index = 0; index < selected.Length; index++) {
            if ((index & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
            selected[index] = selected[index] with { Binding = remap[selected[index].Binding] };
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(bindings, selected, Attribution);
    }
}
