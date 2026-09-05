namespace Sia.Engine.Rendering;

public sealed class PipelineCache<TKey, TPipeline> : IDisposable
    where TKey : notnull
{
    private readonly object _lock = new();
    private readonly Dictionary<TKey, TPipeline> _pipelines = [];
    private readonly Action<TPipeline>? _release;
    private long _hits;
    private long _misses;
    private bool _disposed;

    public PipelineCache(Action<TPipeline>? release = null)
    {
        _release = release;
    }

    public PipelineCacheStats Stats {
        get {
            lock (_lock) {
                return new PipelineCacheStats(_pipelines.Count, _hits, _misses);
            }
        }
    }

    public TPipeline GetOrCreate(TKey key, Func<TKey, TPipeline> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        lock (_lock) {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_pipelines.TryGetValue(key, out var pipeline)) {
                _hits++;
                return pipeline;
            }
            pipeline = factory(key);
            _pipelines.Add(key, pipeline);
            _misses++;
            return pipeline;
        }
    }

    public bool TryGet(TKey key, out TPipeline? pipeline)
    {
        lock (_lock) {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _pipelines.TryGetValue(key, out pipeline);
        }
    }

    public int Invalidate(Func<TKey, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        lock (_lock) {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var keys = _pipelines.Keys.Where(predicate).ToArray();
            foreach (var key in keys) {
                var pipeline = _pipelines[key];
                _pipelines.Remove(key);
                _release?.Invoke(pipeline);
            }
            return keys.Length;
        }
    }

    public void Clear()
    {
        lock (_lock) {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ReleaseAll();
        }
    }

    public void Dispose()
    {
        lock (_lock) {
            if (_disposed) {
                return;
            }
            _disposed = true;
            ReleaseAll();
        }
    }

    private void ReleaseAll()
    {
        var pipelines = _pipelines.Values.ToArray();
        _pipelines.Clear();
        foreach (var pipeline in pipelines) {
            _release?.Invoke(pipeline);
        }
    }
}
