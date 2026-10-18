using Sia.Math;

namespace Sia.Engine.Rendering;

public sealed partial class SceneTraceData
{
    /// <summary>Replaces live transport surface values, preserving geometry, instances and authored identity.</summary>
    public SceneTraceData WithSurfaces(ReadOnlySpan<float4> surfaces, ulong maximumBytes = 128ul * 1024 * 1024,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsInstanced || surfaces.Length != Packed.Length || (ulong)surfaces.Length * 16 > maximumBytes)
            throw new ArgumentException("Live surfaces must match the existing material table and admitted replacement bytes.");
        for (var at = 0; at < surfaces.Length; at++) {
            cancellationToken.ThrowIfCancellationRequested(); var value = surfaces[at];
            Require(math.all(math.isfinite(value)) && math.all(value.xyz >= 0) && (value.w == 0 || value.w == 1));
        }
        return new(this, surfaces.ToArray());
    }

    /// <summary>
    /// Shares immutable local geometry and surfaces while replacing only validated instance/refit rows.
    /// Topology, identities, bindings and materials must remain identical. Identity remains authored.
    /// </summary>
    public SceneTraceData WithInstances(ReadOnlySpan<uint4> instances, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsInstanced) throw new InvalidOperationException("Instance updates require shared local trace geometry.");
        var previous = Instances.Span;
        Require(instances.Length == previous.Length && instances[..5].SequenceEqual(previous[..5]));
        var top = previous[0]; var order = previous[2].x;
        ValidateNodes(instances, top.y, top.x, top.z, cancellationToken);
        for (uint node = 0; node < top.x; node++) {
            if ((node & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            var at = (int)(top.y + node * 3);
            Require(instances[at].w == previous[at].w && instances[at + 1].w == previous[at + 1].w
                && instances[at + 2].Equals(previous[at + 2]));
        }
        Require(instances.Slice((int)order, (int)(top.w - order)).SequenceEqual(previous.Slice((int)order, (int)(top.w - order))));
        for (uint instance = 0; instance < top.z; instance++) {
            if ((instance & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            var at = (int)(top.w + instance * 5);
            Require(instances[at].x == previous[at].x && instances[at].y == previous[at].y
                && instances[at].z == previous[at].z && instances[at].w <= 1
                && instances[at + 1].Equals(previous[at + 1]));
            for (var row = 2; row < 5; row++) {
                var value = instances[at + row];
                Require(math.all(math.isfinite(Vector(value))) && float.IsFinite(BitConverter.UInt32BitsToSingle(value.w)));
            }
        }
        var tail = checked((int)(top.w + top.z * 5));
        Require(instances[tail..].SequenceEqual(previous[tail..]));
        cancellationToken.ThrowIfCancellationRequested();
        return new(this, instances.ToArray());
    }

    private SceneTraceData(SceneTraceData source, uint4[] instances)
    {
        Geometry = source.Geometry; Packed = source.Packed; Identity = source.Identity; Instances = instances;
        TriangleCount = source.TriangleCount;
        Bounds = instances[0].x == 0 ? new(float3.zero, float3.zero)
            : new(Vector(instances[(int)instances[0].y]), Vector(instances[(int)instances[0].y + 1]));
    }

    private SceneTraceData(SceneTraceData source, float4[] surfaces)
    {
        Geometry = source.Geometry; Instances = source.Instances; Identity = source.Identity; Packed = surfaces;
        TriangleCount = source.TriangleCount; Bounds = source.Bounds;
    }
}
