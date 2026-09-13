using System.Runtime.InteropServices.JavaScript;
using System.Collections.Concurrent;
using Sia.Math;

namespace Sia.Engine.Example;

#if BROWSER
public static partial class Program
{
    internal static BrowserThread BrowserOwner { get; private set; } = null!;
    internal static readonly ConcurrentQueue<(int Width, int Height, int Commands, double Distance, float3 Move, float2 Look, float2 Turn, float Speed)> BrowserInputs = new();

    [JSExport]
    public static Task UpdateBrowserInput(int width, int height, int commands, double distance,
        double moveRight, double moveForward, double moveUp, double lookRight, double lookUp,
        double turnRight, double turnUp, double speed)
    {
        BrowserInputs.Enqueue((width, height, commands, distance,
            new((float)moveRight, (float)moveUp, (float)moveForward), new((float)lookRight, (float)lookUp),
            new((float)turnRight, (float)turnUp), (float)speed));
        return Task.CompletedTask;
    }

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
