namespace Sia.Engine.Mesh;

public readonly record struct MeshHandle(int Id)
{
    public static readonly MeshHandle Invalid = new(-1);

    public bool IsValid => Id >= 0;
}
