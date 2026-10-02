using System.Runtime.InteropServices;
using Sia.Engine.Rendering;

namespace Sia.Engine.Example;

public static partial class Program
{
    private static async Task<DiffuseProbeAsset> LoadProbesAsync(string path, HttpClient http)
    {
        using var input = await AssetInput.OpenAsync(path, http);
        const int maximumBytes = 76 + DiffuseProbeAsset.MaximumProbes * 9 * 16 + 32;
        var bytes = await AssetInput.ReadBoundedAsync(input, maximumBytes);
        if (!MemoryMarshal.TryGetArray(bytes, out var segment))
            throw new InvalidOperationException("Asset input must return an array-backed payload.");
        using var payload = new MemoryStream(segment.Array!, segment.Offset, segment.Count, writable: false);
        return DiffuseProbeAsset.Read(payload);
    }

    private static async Task<IblEnvironmentAsset> LoadEnvironmentAsync(string path, HttpClient http)
    {
        using var input = await AssetInput.OpenAsync(path, http);
        var bytes = await AssetInput.ReadExactAsync(input, IblEnvironmentAsset.EncodedBytes);
        using var memory = new MemoryStream(bytes, writable: false);
        return IblEnvironmentAsset.Read(memory);
    }
}
