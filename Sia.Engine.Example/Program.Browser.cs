using System.Runtime.InteropServices.JavaScript;

namespace Sia.Engine.Example;

#if BROWSER
public static partial class Program
{
    internal static BrowserThread BrowserOwner { get; private set; } = null!;
    internal readonly record struct BrowserInput(int Width, int Height, int Commands, double Distance);
    private static readonly object s_BrowserInputLock = new();
    private static BrowserInput? s_BrowserInput;

    [JSExport]
    public static Task UpdateBrowserInput(int width, int height, int commands, double distance)
    {
        lock (s_BrowserInputLock) {
            s_BrowserInput = new(width, height, commands | (s_BrowserInput?.Commands ?? 0),
                (commands & 256) != 0 ? distance : s_BrowserInput?.Distance ?? double.NaN);
        }
        return Task.CompletedTask;
    }

    internal static BrowserInput? TakeBrowserInput()
    {
        lock (s_BrowserInputLock) {
            var input = s_BrowserInput;
            s_BrowserInput = null;
            return input;
        }
    }

    [JSImport("setLoadingState", "main.js")]
    internal static partial void SetLoadingState(string stage, double progress);

    [JSImport("setSceneReady", "main.js")]
    internal static partial void SetSceneReady();

    [JSImport("showError", "main.js")]
    internal static partial void ShowError(string message);
}
#endif
