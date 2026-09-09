using System.Runtime.InteropServices.JavaScript;

namespace Sia.Engine.Example;

#if BROWSER
public static partial class Program
{
    [JSImport("setLoadingState", "main.js")]
    internal static partial void SetLoadingState(string stage, double progress);

    [JSImport("setSceneReady", "main.js")]
    internal static partial void SetSceneReady();

    private static async Task<ReadOnlyMemory<byte>> DownloadSceneAsync(string path)
    {
        SetLoadingState("Loading scene", double.NaN);
        using var client = new HttpClient();
        using var response = await client.GetAsync(path, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        var stage = response.Headers.TryGetValues("X-Sia-Asset-Cache", out var cache)
            && cache.Contains("hit") ? "Loading cached scene" : "Loading scene";
        var length = response.Content.Headers.ContentLength;
        const int maximumBytes = 512 * 1024 * 1024;
        if (length > maximumBytes) { throw new InvalidDataException("The scene download exceeds the supported size."); }
        using var bytes = new MemoryStream(length is > 0 ? (int)length.Value : 0);
        using var stream = await response.Content.ReadAsStreamAsync();
        var buffer = new byte[1024 * 1024];
        long reported = 0;
        int count;
        while ((count = await stream.ReadAsync(buffer)) != 0) {
            if (bytes.Length + count > maximumBytes) { throw new InvalidDataException("The scene download exceeds the supported size."); }
            bytes.Write(buffer, 0, count);
            if (bytes.Length - reported >= 1024 * 1024) {
                reported = bytes.Length;
                SetLoadingState(stage, length is > 0 ? (double)reported / length.Value : double.NaN);
            }
        }
        SetLoadingState(stage, 1);
        return bytes.GetBuffer().AsMemory(0, checked((int)bytes.Length));
    }
}
#endif
