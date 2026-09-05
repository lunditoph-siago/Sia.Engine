namespace Sia.Engine.Rendering;

public sealed class RenderResourceCollection
{
    private readonly Dictionary<Type, object> _resources = [];

    public int Count => _resources.Count;

    public void Set<T>(T resource)
        where T : notnull =>
        _resources[typeof(T)] = resource;

    public T GetRequired<T>()
        where T : notnull =>
        _resources.TryGetValue(typeof(T), out var resource)
            ? (T)resource
            : throw new KeyNotFoundException(
                $"Render resource '{typeof(T)}' is not available.");

    public T GetOrAdd<T>(Func<T> factory)
        where T : notnull
    {
        ArgumentNullException.ThrowIfNull(factory);
        if (_resources.TryGetValue(typeof(T), out var resource)) {
            return (T)resource;
        }

        var created = factory();
        _resources.Add(typeof(T), created);
        return created;
    }

    public bool TryGet<T>(out T? resource)
        where T : notnull
    {
        if (_resources.TryGetValue(typeof(T), out var value)) {
            resource = (T)value;
            return true;
        }

        resource = default;
        return false;
    }

    public bool Remove<T>()
        where T : notnull =>
        _resources.Remove(typeof(T));

    public void Clear() => _resources.Clear();
}
