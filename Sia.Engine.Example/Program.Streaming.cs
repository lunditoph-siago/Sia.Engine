using Sia.Asset;
using Sia.Engine.Rendering.Pbr;

namespace Sia.Engine.Example;

public static partial class Program
{
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
