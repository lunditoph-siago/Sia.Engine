using Sia;

namespace Sia.Engine.Rendering;

public sealed class RenderWorld : IDisposable
{
    private readonly Dictionary<RenderViewKey, RenderView> _views = [];
    private bool _disposed;

    public World Entities { get; } = new();
    public RenderResourceCollection Resources { get; } = new();

    public IReadOnlyCollection<RenderView> Views => _views.Values;

    public ulong FrameIndex { get; private set; }

    public RenderView GetOrCreateView(RenderViewKey key)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_views.TryGetValue(key, out var view)) return view;

        view = new RenderView(key);
        _views.Add(key, view);
        return view;
    }

    public bool RemoveView(RenderViewKey key)
    {
        if (!_views.Remove(key, out var view)) return false;
        view.PersistentResources.DisposeOwned();
        return true;
    }

    public void BeginFrame()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        FrameIndex++;
        foreach (var view in _views.Values) {
            view.BeginFrame();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;
        foreach (var view in _views.Values)
            view.PersistentResources.DisposeOwned();
        _views.Clear();
        Resources.DisposeOwned();
        Entities.Dispose();
    }
}
