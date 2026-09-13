using System.Runtime.InteropServices.JavaScript;

namespace Sia.Engine.Example;

#if BROWSER
internal sealed partial class SceneExampleApp
{
    [JSImport("nextAnimationFrame", "main.js")]
    private static partial Task<double> NextAnimationFrame();

    private async Task RunAnimationFrameLoopAsync()
    {
        while (true) {
            var timestamp = await NextAnimationFrame();
            CaptureBrowserState();
            var running = await Program.BrowserOwner.RunGraphicsAsync(() =>
                Task.FromResult(RenderAnimationFrame(timestamp)));
            PublishBrowserState();
            if (!running) return;
        }
    }
}
#endif
