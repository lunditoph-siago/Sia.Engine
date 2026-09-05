namespace Sia.Engine.Rendering;

public readonly record struct PipelineCacheStats(int Count, long Hits, long Misses);
