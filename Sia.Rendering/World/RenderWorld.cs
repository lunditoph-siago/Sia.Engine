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

    internal void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    internal bool ContainsView(RenderView? view)
        => view is not null && _views.TryGetValue(view.Key, out var current) && ReferenceEquals(current, view);

    public RenderView GetOrCreateView(RenderViewKey key)
    {
        ThrowIfDisposed();
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
        ThrowIfDisposed();
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
