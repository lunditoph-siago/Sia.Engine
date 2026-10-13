#if !BROWSER
using Sia;
using Sia.Asset;
using Sia.Engine.Rendering;
using Sia.Engine.Rendering.Pbr;

namespace Sia.Engine.Example;

public static partial class Program
{
    // Offline use-site composition. The returned transport owns its data, not asset references.
    internal static async Task<SceneTraceData> PrepareReferenceProbeTraceAsync(string path, HttpClient client,
        int maximumFileBytes = 128 * 1024 * 1024, long maximumTotalBytes = 512L * 1024 * 1024,
        ulong maximumTraceBytes = 128ul * 1024 * 1024, CancellationToken cancellationToken = default)
    {
        var prepared = await PbrSceneFiles.PrepareBvhAsync(path, client, maximumFileBytes, maximumTotalBytes, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        using var world = new World();
        world.AddAddon<AssetLibrary>();
        var acquired = PbrSceneFiles.AcquireBvh(world, prepared);
        try {
            cancellationToken.ThrowIfCancellationRequested();
            var trace = PbrSceneTransport.Build(acquired.Scene, maximumTraceBytes, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return trace;
        }
        finally { acquired.Root.Destroy(); }
    }
}
#endif
