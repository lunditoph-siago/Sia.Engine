using Sia.Asset;
using Sia.Engine.Mesh;
using Sia.Engine.Rendering.Pbr;

namespace Sia.Engine.Example;

public static partial class Program
{
    private static async Task<PbrSceneStream> OpenBunnyStreamAsync(MeshPatchAsset asset)
    {
#if BROWSER
        SetLoadingState("Preparing Bunny LOD pages", double.NaN);
        var packed = await BrowserOwner.RunCpuAsync(Pack);
#else
        var packed = await Task.Run(() => Pack(CancellationToken.None));
#endif
        var stream = await PbrSceneStream.OpenAsync(packed.Metadata, (chunk, token) => {
            token.ThrowIfCancellationRequested();
            return ValueTask.FromResult<Stream>(new MemoryStream(packed.Chunks[chunk.Id], writable: false));
        });
        Console.WriteLine($"Bunny LOD stream: {packed.Chunks.Count} shared source chunks; {stream.Instances.Length} instances.");
        return stream;

        (byte[] Metadata, Dictionary<string, byte[]> Chunks) Pack(CancellationToken token)
        {
            var chunks = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            var source = SceneExampleApp.CreateBunnyScene(asset);
            var metadata = PbrSceneStream.CookAsync(source, (chunk, bytes, cancellation) => {
                cancellation.ThrowIfCancellationRequested();
                chunks.Add(chunk.Id, bytes.ToArray());
                return ValueTask.CompletedTask;
            }, token).GetAwaiter().GetResult();
            return (metadata, chunks);
        }
    }

    private static async Task<PbrSceneStream> OpenStreamAsync(string path, HttpClient client)
    {
#if BROWSER
        SetLoadingState("Preparing the initial scene", double.NaN);
#endif
        using var body = await AssetInput.OpenAsync(path, client);
        var metadata = await AssetInput.ReadBoundedAsync(body, PbrSceneStream.MaximumMetadataBytes);
        Func<AssetChunk, CancellationToken, ValueTask<Stream>> open;
        if (AssetInput.TryGetHttpUri(path, out var uri)) {
            open = AssetChunkSources.Http(client, new Uri(uri!, "."));
        }
        else {
            open = AssetChunkSources.Directory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        }
        var scene = await PbrSceneStream.OpenAsync(metadata, open);
        Console.WriteLine($"PBR stream bootstrap: {scene.Statistics.ReadBytes} chunk bytes; {scene.Instances.Length + scene.Bootstrap.Instances.Length} instances.");
        return scene;
    }

#if !BROWSER
    private static async Task CookStreamAsync(string path, string directory)
    {
        if (Directory.Exists(directory))
            throw new IOException("Choose a new output directory.");
        var asset = PbrSceneAsset.Decode(await File.ReadAllBytesAsync(path), 1024 * 1024 * 1024);
        Directory.CreateDirectory(directory);
        var metadata = await PbrSceneStream.CookAsync(asset, async (chunk, bytes, token) => {
            await using var file = new FileStream(Path.Combine(directory, chunk.FileName), FileMode.CreateNew, FileAccess.Write);
            await file.WriteAsync(bytes, token);
        });
        await File.WriteAllBytesAsync(Path.Combine(directory, "Bistro.siastream"), metadata);
        Console.WriteLine($"Cooked scene stream: {directory}");
    }
#endif
}
