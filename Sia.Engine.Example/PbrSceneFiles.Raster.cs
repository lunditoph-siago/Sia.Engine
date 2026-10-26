using Sia;
using Sia.Asset;
using Sia.Engine.Mesh;
using Sia.Engine.Rendering.Pbr;

namespace Sia.Engine.Example;

internal static partial class PbrSceneFiles
{
    internal sealed record PreparedRasterStreaming(Prepared<MeshRasterRecord> Index, PageSource Source)
        : Prepared(Index.Id, Index.Scene, Index.Meshes, Index.Materials, Index.Textures), IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Source.DisposeAsync();
    }

    internal static async Task<PreparedRasterStreaming> PrepareRasterStreamingAsync(string path, HttpClient client,
        int maximumFileBytes = 128 * 1024 * 1024, long maximumTotalBytes = 512L * 1024 * 1024,
        CancellationToken cancellationToken = default)
    {
        var prepared = await PrepareRasterAsync(path, client, maximumFileBytes, maximumTotalBytes, cancellationToken);
        var source = new PageSource(path, client, new(prepared.Id, prepared.Scene, prepared.Meshes,
            new Dictionary<AssetId, MeshVirtualRecord>(), prepared.Materials, prepared.Textures));
        return new(prepared, source) { EncodedBytes = prepared.EncodedBytes };
    }

    internal static (Entity Root, PbrRasterScene Scene, PbrSceneStream? Stream) AcquireRasterStreaming(World world, PreparedRasterStreaming prepared)
    {
        var acquired = AcquireRaster(world, prepared.Index);
        try {
            var stream = prepared.Textures.Values.Any(texture => texture.Mips is not null)
                ? PbrSceneStream.Create(acquired.Scene, TextureMips(world, prepared), prepared.Source.ReadMipAsync) : null;
            return (acquired.Root, acquired.Scene, stream);
        }
        catch { acquired.Root.Destroy(); throw; }
    }
}
