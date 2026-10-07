using Sia.Asset;
using Sia.Engine.Rendering;
using Sia.Engine.Rendering.Pbr;

namespace Sia.Engine.Example;

public static partial class Program
{
    private static async Task<DiffuseProbeAsset?> LoadLightmapProbesAsync(string path, HttpClient http)
    {
        var sibling = AssetInput.TryGetHttpUri(path, out var uri)
            ? new Uri(uri!, "probes.siaprobe").AbsoluteUri
            : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "probes.siaprobe");
        try { return await LoadProbesAsync(sibling, http); }
        // Legacy page bundles have no probe seed. Corrupt or inaccessible present seeds must still fail.
        catch (FileNotFoundException) { return null; }
        catch (HttpRequestException error) when (error.StatusCode == System.Net.HttpStatusCode.NotFound) { return null; }
    }

    private static async Task<PbrLightmapStream> OpenLightmapStreamAsync(string path, HttpClient http)
    {
        using var input = await AssetInput.OpenAsync(path, http);
        var metadata = await AssetInput.ReadBoundedAsync(input, PbrLightmapStream.MaximumMetadataBytes);
        var open = AssetInput.TryGetHttpUri(path, out var uri)
            ? AssetChunkSources.Http(http, new Uri(uri!, "."))
            : AssetChunkSources.Directory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var stream = await PbrLightmapStream.OpenAsync(metadata, open);
        Console.WriteLine($"Lightmap stream bootstrap: {metadata.Length} metadata bytes; {stream.Resolution} virtual square; {stream.Receivers.Length} receivers; {stream.Pages.Length} page addresses; {stream.Statistics.ReadBytes} fine bytes.");
        return stream;
    }

    private static Task<PbrLightmapAsset> LoadLightmapsAsync(string path, HttpClient http)
        => AssetInput.LoadBoundedAsync(path, http, 32 * 1024 * 1024,
            static bytes => PbrLightmapAsset.Decode(bytes.Span));

    private static Task<DiffuseProbeAsset> LoadProbesAsync(string path, HttpClient http)
    {
        const int maximumBytes = 76 + DiffuseProbeAsset.MaximumProbes * 9 * 16 + 32;
        return AssetInput.LoadBoundedAsync(path, http, maximumBytes,
            static bytes => AssetInput.Decode(bytes, DiffuseProbeAsset.Read));
    }

    private static Task<SceneTraceData> LoadStaticTransportAsync(string path, HttpClient http)
        => AssetInput.LoadBoundedAsync(path, http, 128 * 1024 * 1024 + 84,
            static bytes => SceneTraceData.Decode(bytes));

    private static Task<IblEnvironmentAsset> LoadEnvironmentAsync(string path, HttpClient http)
        => AssetInput.LoadExactAsync(path, http, IblEnvironmentAsset.EncodedBytes, IblEnvironmentAsset.Read);
}
