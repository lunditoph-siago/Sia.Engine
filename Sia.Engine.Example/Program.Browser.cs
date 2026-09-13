using System.Runtime.InteropServices.JavaScript;

namespace Sia.Engine.Example;

#if BROWSER
public static partial class Program
{
    internal static BrowserThread BrowserOwner { get; private set; } = null!;

    [JSImport("setLoadingState", "main.js")]
    internal static partial void SetLoadingState(string stage, double progress);

    [JSImport("setSceneReady", "main.js")]
    internal static partial void SetSceneReady();

    [JSImport("showError", "main.js")]
    internal static partial void ShowError(string message);

    private static async Task<ReadOnlyMemory<byte>> DownloadSceneAsync(string path)
    {
        BrowserOwner.VerifyAccess();
        using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        return await SceneDownload.DownloadAsync(client, path, SetLoadingState);
    }
}
#endif
