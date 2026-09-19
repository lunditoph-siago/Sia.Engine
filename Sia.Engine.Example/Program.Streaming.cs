using System.Diagnostics;
using Sia.Asset;
using Sia.Engine.Rendering.Pbr;

namespace Sia.Engine.Example;

public static partial class Program
{
    private static async Task<PbrSceneStream> OpenStreamAsync(string path, HttpClient client)
    {
        var timer = Stopwatch.StartNew();
#if BROWSER
        SetLoadingState("Preparing the initial scene", double.NaN);
#endif
        byte[] metadata;
        Func<AssetChunk, CancellationToken, ValueTask<Stream>> open;
        if (Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https") {
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            using var body = await response.Content.ReadAsStreamAsync();
            metadata = await ReadMetadataAsync(body);
            open = AssetChunkSources.Http(client, new Uri(uri, "."));
        } else {
            using var body = File.OpenRead(path); metadata = await ReadMetadataAsync(body);
            open = AssetChunkSources.Directory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        }
        var scene = await PbrSceneStream.OpenAsync(metadata, open);
        Console.WriteLine($"PBR stream bootstrap: {timer.Elapsed.TotalMilliseconds:F2} ms; {scene.Statistics.ReadBytes} chunk bytes; {scene.Instances.Length + scene.Bootstrap.Instances.Length} instances.");
        return scene;
    }
    private static async Task<byte[]> ReadMetadataAsync(Stream stream)
    {
        using var output = new MemoryStream(); var buffer = new byte[65536];
        while (true) {
            var count = await stream.ReadAsync(buffer);
            if (count == 0) return output.ToArray();
            if (output.Length + count > AssetChunkManifest.MaximumBytes) throw new InvalidDataException("Scene metadata exceeds its limit.");
            output.Write(buffer, 0, count);
        }
    }
#if !BROWSER
    private static async Task CookStreamAsync(string path, string directory)
    {
        if (Directory.Exists(directory)) throw new IOException("Choose a new output directory.");
        var timer = Stopwatch.StartNew();
        var asset = PbrSceneAsset.Decode(await File.ReadAllBytesAsync(path), 1024 * 1024 * 1024);
        Directory.CreateDirectory(directory);
        var metadata = await PbrSceneStream.CookAsync(asset, async (chunk, bytes, token) => {
            await using var file = new FileStream(Path.Combine(directory, chunk.FileName), FileMode.CreateNew, FileAccess.Write);
            await file.WriteAsync(bytes, token);
        });
        await File.WriteAllBytesAsync(Path.Combine(directory, "Bistro.siastream"), metadata);
        Console.WriteLine($"Cooked scene stream in {timer.Elapsed.TotalSeconds:F2} s: {directory}");
    }
#endif
}
