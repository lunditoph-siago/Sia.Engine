namespace Sia.Engine.Rendering.Pbr;

public sealed class PbrDrawItemComparer : IComparer<PbrDrawItem>
{
    public static PbrDrawItemComparer Instance { get; } = new();

    private PbrDrawItemComparer()
    {
    }

    public int Compare(PbrDrawItem left, PbrDrawItem right)
    {
        var mesh = left.Mesh.Id.CompareTo(right.Mesh.Id);
        return mesh != 0 ? mesh : left.InstanceIndex.CompareTo(right.InstanceIndex);
    }
}
