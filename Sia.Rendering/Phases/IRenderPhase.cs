namespace Sia.Engine.Rendering;

public interface IRenderPhase
{
    public Type ItemType { get; }

    public int Count { get; }

    public void Clear();
}
