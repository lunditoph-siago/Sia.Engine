namespace Sia.Engine.Rendering;

public sealed class RenderView
{
    internal RenderView(RenderViewKey key)
    {
        Key = key;
    }

    public RenderViewKey Key { get; }

    public RenderResourceCollection PersistentResources { get; } = new();

    public RenderResourceCollection Resources { get; } = new();

    public RenderPhaseCollection Phases { get; } = new();

    internal void BeginFrame()
    {
        Resources.Clear();
        Phases.Clear();
    }
}
