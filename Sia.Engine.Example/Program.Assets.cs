using Sia.Engine.Mesh;
using Sia.Engine.Rendering.Pbr;

namespace Sia.Engine.Example;

public static partial class Program
{
    private static async Task<MeshPatchAsset> LoadBunnyAsync()
    {
        using var stream = typeof(Program).Assembly.GetManifestResourceStream("Sia.Engine.Example.bunny.siapatch")
            ?? throw new FileNotFoundException("The bundled Bunny asset is missing.");
        Console.WriteLine("Stanford Bunny: Stanford University Computer Graphics Laboratory; https://graphics.stanford.edu/data/3Dscanrep/");
        var bytes = await AssetInput.ReadExactAsync(stream, checked((int)stream.Length));
#if BROWSER
        var asset = await BrowserOwner.RunCpuAsync(token => MeshPatchAsset.Decode(bytes, cancellationToken: token));
#else
        var asset = MeshPatchAsset.Decode(bytes);
#endif
        Console.WriteLine($"Patch asset: {bytes.Length} bytes; {asset.SourceHash}.");
        return asset;
    }

    private static async Task<PbrSceneAsset> LoadPbrAsync(string? path, bool finest, HttpClient client)
    {
#if BROWSER
        BrowserOwner.VerifyAccess();
        // SceneDownload owns an activity deadline, so long transfers need no total HTTP timeout.
        using var downloadClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var bytes = await SceneDownload.DownloadAsync(downloadClient,
            path ?? throw new ArgumentException("The browser entry point must supply the published scene URL."),
            SetLoadingState);
        SetLoadingState("Preparing geometry and textures", double.NaN);
        await Task.Delay(20);
#else
        var source = path ?? Path.Combine(AppContext.BaseDirectory, "Assets",
            finest ? "BistroFinest.siapbr" : "Bistro.siapbr");
        using var input = await AssetInput.OpenAsync(source, client);
        var bytes = await AssetInput.ReadBoundedAsync(input, int.MaxValue);
#endif
#if BROWSER
        var scene = await BrowserOwner.RunCpuAsync(token => PbrSceneAsset.Decode(bytes.Span, 1024 * 1024 * 1024, token));
#else
        var scene = PbrSceneAsset.Decode(bytes.Span, 1024 * 1024 * 1024);
#endif
        Console.WriteLine(scene.Attribution);
        Console.WriteLine($"PBR scene: {bytes.Length} bytes; "
            + $"{scene.Geometry.Length} shared geometries, {scene.Materials.Length} materials, {scene.Instances.Length} instances.");
        return scene;
    }
}
