#if !BROWSER
using System.Security.Cryptography;
using Sia.Asset;
using Sia.Engine.Rendering;
using Sia.Engine.Rendering.Pbr;

namespace Sia.Engine.Example;

internal static partial class ExampleContent
{
    private static async Task AddReflectionsAsync(Entry entry, PbrSceneFiles.Prepared scene, HttpClient client,
        Dictionary<string, string> files, Action<long> admit, int maximumFileBytes, long remainingBytes, CancellationToken token)
    {
        if ((entry.Environment is not null || entry.ReflectionCaptures.Length != 0) && maximumFileBytes < IblEnvironmentAsset.EncodedBytes)
            throw new InvalidDataException("Selected environments exceed per-file admission.");
        var sceneIds = ReflectionCaptureFiles.SceneIds(scene);
        var environments = new Dictionary<AssetId, string>(); var captures = new HashSet<AssetId>();
        string? sourceHash = null;
        AssetId Id(string path) {
            if (!path.EndsWith(".siaasset", StringComparison.OrdinalIgnoreCase)
                || !Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out var id) || id == Guid.Empty)
                throw new InvalidDataException("Reflection inputs require nonempty UUID-named .siaasset records.");
            return new(id);
        }
        async Task EnvironmentFile(AssetId id, string hash, string path) {
            token.ThrowIfCancellationRequested();
            if (sceneIds.Contains(id) || captures.Contains(id)) throw new InvalidAssetException("Environment aliases another selected asset kind.");
            var name = id.Value.ToString("N") + ".siaasset";
            if (environments.TryGetValue(id, out var previous)) {
                if (previous != hash) throw new InvalidAssetException("Selected environments disagree on a logical revision.");
            }
            else {
                if (!files.TryAdd(name, path)) throw new InvalidAssetException("Environment aliases another selected asset kind.");
                admit(IblEnvironmentAsset.EncodedBytes); environments.Add(id, hash);
            }
            // Authenticate every independently named file, including equal-content aliases and separately published inputs.
            using var input = File.OpenRead(path);
            if (input.Length != IblEnvironmentAsset.EncodedBytes || input.Length > maximumFileBytes
                || Convert.ToHexString(await SHA256.HashDataAsync(input, token)) != hash)
                throw new InvalidDataException("Selected environment differs from its declared content or byte admission.");
        }
        if (entry.Environment is { } environmentPath) {
            token.ThrowIfCancellationRequested(); var id = Id(environmentPath);
            if (new FileInfo(environmentPath).Length != IblEnvironmentAsset.EncodedBytes || IblEnvironmentAsset.EncodedBytes > maximumFileBytes
                || IblEnvironmentAsset.EncodedBytes > remainingBytes)
                throw new InvalidDataException("Selected environment exceeds its encoded admission.");
            var record = FileLoader.Load<IblEnvironmentRecord>(environmentPath); sourceHash = record.Data.Write(Stream.Null);
            await EnvironmentFile(id, sourceHash, environmentPath);
        }
        foreach (var path in entry.ReflectionCaptures) {
            token.ThrowIfCancellationRequested(); var id = Id(path);
            if (sceneIds.Contains(id) || environments.ContainsKey(id) || !captures.Add(id)
                || !files.TryAdd(id.Value.ToString("N") + ".siaasset", path))
                throw new InvalidAssetException("Capture aliases another selected asset or repeats a region.");
            var bytes = new FileInfo(path).Length;
            if (bytes > 1024 * 1024) throw new InvalidDataException("Selected capture exceeds its metadata admission.");
            admit(bytes);
            var prepared = await ReflectionCaptureFiles.PrepareAsync(path, client, scene,
                maximumMetadataBytes: (ulong)System.Math.Min(maximumFileBytes, 1024 * 1024), cancellationToken: token);
            var capture = prepared.Capture.Data;
            sourceHash ??= capture.SourceEnvironment.ContentHash;
            if (sourceHash != capture.SourceEnvironment.ContentHash)
                throw new InvalidAssetException("Default capture regions require the selected source environment.");
            foreach (var reference in new[] { capture.SourceEnvironment, capture.Environment })
                await EnvironmentFile(reference.Id, reference.ContentHash,
                    Path.Combine(Path.GetDirectoryName(path)!, reference.Id.Value.ToString("N") + ".siaasset"));
        }
    }
}
#endif
