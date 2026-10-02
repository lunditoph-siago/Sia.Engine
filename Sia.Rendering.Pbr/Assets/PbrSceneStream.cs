using System.Text.Json.Serialization;
using Sia.Asset;
using Sia.Math;

namespace Sia.Engine.Rendering.Pbr;

public sealed partial class PbrSceneStream : IAsyncDisposable
{
    private sealed record Instance(int Geometry, int Material, float[] Transform);

    [JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
    [JsonSerializable(typeof(Header))]
    private partial class StreamJsonContext : JsonSerializerContext;

    private readonly AssetChunkCache _cache;
    private readonly AssetChunkManifest _manifest;

    public ReadOnlyMemory<VisibilityInstance> Instances { get; }
    public PbrSceneAsset Bootstrap { get; }
    public Aabb Bounds { get; }

    public AssetChunkCacheStatistics Statistics => _cache.Statistics;

    public ValueTask DisposeAsync() => _cache.DisposeAsync();
}
