using Sia.Asset;
using System.Runtime.InteropServices;

namespace Sia.Engine.Example;

internal static partial class PbrSceneFiles
{
    internal static async Task<AssetPackIndex?> ReadPackIndexAsync(string? path, HttpClient client,
        CancellationToken token = default)
    {
        if (path is null) return null;
        var name = Path.GetFileName(AssetInput.TryGetHttpUri(path, out var uri) ? uri!.AbsolutePath : path);
        if (!name.EndsWith(".siapack", StringComparison.Ordinal))
            throw new InvalidDataException("A storage index requires its content hash and .siapack suffix.");
        var hash = name[..^8];
        _ = new AssetChunk(hash, 1); // Validate the expected SHA before any IO.
        using var input = await AssetInput.OpenAsync(path, client, token);
        var bytes = await AssetInput.ReadBoundedAsync(input, AssetPackIndex.MaximumEncodedBytes, token);
        AssetPackIndex Decode(CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!MemoryMarshal.TryGetArray(bytes, out var segment)) throw new InvalidOperationException("Expected owned index input.");
            using var encoded = new MemoryStream(segment.Array!, segment.Offset, segment.Count, writable: false);
            return AssetPackIndex.Load(encoded, hash, cancellationToken: cancellation);
        }
#if BROWSER
        return await Program.BrowserOwner.RunCpuAsync(Decode, token);
#else
        return Decode(token);
#endif
    }
}
