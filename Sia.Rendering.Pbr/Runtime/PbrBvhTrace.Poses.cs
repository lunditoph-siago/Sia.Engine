namespace Sia.Engine.Rendering.Pbr;

public sealed partial class PbrBvhTrace
{
    /// <summary>Copies exact current authored-ID poses in original trace-row order; includes live hide state, without recooking or acquiring.</summary>
    public void CopyPoses(Span<PbrReflectionInstancePose> destination)
    {
        if (destination.Length != _instances.Length) throw new ArgumentException("Pose storage must cover the trace instance table.", nameof(destination));
        for (var at = 0; at < destination.Length; at++)
            destination[at] = new(Scene.Instances[at].Id, _instances[at].Transform, !_instances[at].Disabled);
    }
}
