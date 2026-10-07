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
    private static int[] ParseConventionalGeometry(string option, string value)
    {
        if (option != "--conventional-geometry")
            throw new ArgumentException("Expected --conventional-geometry comma-separated source geometry IDs.");
        var ids = value.Split(',');
        var result = new int[ids.Length];
        for (var i = 0; i < ids.Length; i++)
            if (!int.TryParse(ids[i], System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out result[i]) || result[i] < 0)
                throw new ArgumentException("Conventional geometry IDs must be nonnegative integers.");
        if (result.Distinct().Count() != result.Length)
            throw new ArgumentException("Conventional geometry IDs must be unique.");
        return result;
    }

    private static async Task CookStreamAsync(string path, string directory, int[] conventional)
    {
        var destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (Directory.Exists(destination) || File.Exists(destination))
            throw new IOException("Choose a new output directory.");
        var asset = PbrSceneAsset.Decode(await File.ReadAllBytesAsync(path), 1024 * 1024 * 1024);
        var staging = Path.Combine(Path.GetDirectoryName(destination)!, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        if (File.Exists(staging) || Directory.Exists(staging)) throw new IOException("Stream staging path already exists.");
        Directory.CreateDirectory(staging);
        try {
            var metadata = await PbrSceneStream.CookAsync(asset, async (chunk, bytes, token) => {
                await using var file = new FileStream(Path.Combine(staging, chunk.FileName), FileMode.CreateNew, FileAccess.Write);
                await file.WriteAsync(bytes, token);
            }, conventional);
            await using (var check = await PbrSceneStream.OpenAsync(metadata, AssetChunkSources.Directory(staging))) {
                if (!check.StaticIdentity.Span.SequenceEqual(PbrSceneTransport.StaticIdentity(asset)))
                    throw new InvalidDataException("Cooked stream changed the static source domain.");
                if (!check.LightmapIdentity.Span.SequenceEqual(PbrLightmapAsset.Identity(asset)))
                    throw new InvalidDataException("Cooked stream changed the complete lightmap source domain.");
            }
            await File.WriteAllBytesAsync(Path.Combine(staging, "Bistro.siastream"), metadata);
            Directory.Move(staging, destination);
            Console.WriteLine($"Cooked scene stream: {destination}; {conventional.Length} conventional geometry assets.");
        }
        finally {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }
#endif
}
