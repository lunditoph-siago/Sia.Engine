using System.Runtime.InteropServices.JavaScript;
using System.Collections.Concurrent;

namespace Sia.Engine.Example;

#if BROWSER
public static partial class Program
{
    internal static BrowserThread BrowserOwner { get; private set; } = null!;
    internal static readonly ConcurrentQueue<(int Width, int Height, int Commands, double Distance)> BrowserInputs = new();

    [JSExport]
    public static Task UpdateBrowserInput(int width, int height, int commands, double distance)
    {
        BrowserInputs.Enqueue((width, height, commands, distance));
        return Task.CompletedTask;
    }

    [JSImport("setLoadingState", "main.js")]
    internal static partial void SetLoadingState(string stage, double progress);

    [JSImport("setSceneReady", "main.js")]
    internal static partial void SetSceneReady();

    [JSImport("showError", "main.js")]
    internal static partial void ShowError(string message);
}
#endif
