#if !BROWSER
using System.Security.Cryptography;
using Sia;
using Sia.Asset;
using Sia.Engine.Mesh;
using Sia.Engine.Rendering;
using Sia.Engine.Rendering.Pbr;
using Sia.Math;

namespace Sia.Engine.Example;

// Offline publication transaction at the use site. Asset core has no staging or domain policy.
internal static partial class ReflectionCaptureCook
{
    internal readonly record struct Result(AssetId Id, IReadOnlyList<string> Files, ulong EncodedBytes);

    internal static Result Cook(PbrSceneFiles.Prepared<MeshBvhRecord> input, IblEnvironmentRecord source,
        AssetId sourceId, AssetId environmentId, AssetId captureId, ReadOnlySpan<Guid> staticInstances,
        float3 position, Aabb bounds, string outputDirectory, PbrReflectionCaptureSettings? settings = null,
        ulong maximumWorkingBytes = 32ul * 1024 * 1024, ulong maximumStagingBytes = 4ul * 1024 * 1024,
        CancellationToken cancellationToken = default, Func<string, Stream, Stream>? decorateOutput = null)
    {
        ArgumentNullException.ThrowIfNull(input); ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory); cancellationToken.ThrowIfCancellationRequested();
        var output = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputDirectory));
        var parent = Path.GetDirectoryName(output);
        if (parent is null || !Directory.Exists(parent)) throw new DirectoryNotFoundException("Capture output requires an existing parent.");
        RequireDirectory(parent);
        if (Path.Exists(output)) throw new IOException("Capture output must not already exist.");
        var claims = ReflectionCaptureFiles.SceneIds(input);
        if (claims.Contains(sourceId) || claims.Contains(environmentId) || claims.Contains(captureId)
            || !sourceId.IsValid || !environmentId.IsValid || !captureId.IsValid
            || captureId == sourceId || captureId == environmentId)
            throw new InvalidAssetException("Capture publication identities alias another selected asset.");
        if (staticInstances.Length is < 1 or > PbrReflectionCaptureData.MaximumStaticInstances)
            throw new ArgumentException("Capture requires a bounded static domain.", nameof(staticInstances));
        var metadataBytes = 308ul + (ulong)staticInstances.Length * 16;
        var encodedBytes = metadataBytes + (ulong)IblEnvironmentAsset.EncodedBytes * (sourceId == environmentId ? 1ul : 2ul);
        if (encodedBytes > maximumStagingBytes) throw new ArgumentException("Capture publication exceeds staging admission.", nameof(maximumStagingBytes));
        // Reserve known serializer payload and metadata snapshots alongside the producer's own complete scratch admission.
        var fixedBytes = 2 * (metadataBytes + (ulong)staticInstances.Length * 16 + 648)
            + 4ul * IblEnvironmentAsset.EncodedBytes + (ulong)staticInstances.Length * (ulong)System.Runtime.CompilerServices.Unsafe.SizeOf<PbrMeshInstance>() + 8192ul;
        if (fixedBytes >= maximumWorkingBytes) throw new ArgumentException("Capture cannot fit its working admission.", nameof(maximumWorkingBytes));
        var sourceHash = ReflectionCaptureFiles.EnvironmentHash(source.Data);
        using var world = new World(); world.AddAddon<AssetLibrary>();
        var acquired = PbrSceneFiles.AcquireBvh(world, input);
        try {
            var selected = acquired.Scene.SelectInstances(staticInstances, maximumWorkingBytes - fixedBytes, cancellationToken);
            // Create a preliminary pure value to reject region/settings/IDs before sampling or filesystem effects.
            var recipe = PbrReflectionCaptureData.Create(captureId, new(input.Id), Convert.ToHexString(selected.TransportIdentity.Span),
                new(sourceId, sourceHash), new(environmentId, sourceHash), position, bounds, settings ?? new(), staticInstances,
                maximumWorkingBytes - fixedBytes, cancellationToken);
            AssetLibrary.RegisterAsset<IblEnvironmentResource, IblEnvironmentRecord>();
            var sourceEntity = world.AcquireAsset(sourceId, () => source, acquired.Root);
            var captured = PbrReflectionCaptureBaker.Bake(selected, sourceEntity.Get<IblEnvironmentResource>().Data,
                position, bounds, recipe.Settings, maximumWorkingBytes - fixedBytes, cancellationToken);
            var environmentHash = ReflectionCaptureFiles.EnvironmentHash(captured);
            var data = PbrReflectionCaptureData.Create(captureId, new(input.Id), recipe.SceneIdentity,
                recipe.SourceEnvironment, new(environmentId, environmentHash), position, bounds, recipe.Settings,
                staticInstances, maximumWorkingBytes - fixedBytes, cancellationToken);
            // Equal source/output logical IDs are legal only when no content revision changes.
            AssetLibrary.RegisterAsset<PbrReflectionCaptureResource, PbrReflectionCaptureRecord>();
            var capture = world.AcquireAsset(captureId, () => new PbrReflectionCaptureRecord { Data = data }, acquired.Root);
            var environment = world.AcquireAsset(environmentId, () => new IblEnvironmentRecord { Data = captured }, acquired.Root);
            var stage = Path.Combine(parent, ".reflection-cook-" + Guid.NewGuid().ToString("N"));
            if (Path.Exists(stage)) throw new IOException("Capture staging path already exists.");
            var owned = new List<string>(3); cancellationToken.ThrowIfCancellationRequested(); Directory.CreateDirectory(stage);
            void Write(AssetId id, int length, Action<Stream> encode)
            {
                RequireDirectory(stage); cancellationToken.ThrowIfCancellationRequested();
                var path = Path.Combine(stage, id.Value.ToString("N") + ".siaasset");
                using var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
                owned.Add(path);
                using (var target = decorateOutput?.Invoke(path, file) ?? file) { encode(target); target.Flush(); }
                cancellationToken.ThrowIfCancellationRequested();
                if (new FileInfo(path).Length != length) throw new InvalidDataException("Capture output differs from its admitted size.");
            }
            try {
                Write(sourceId, IblEnvironmentAsset.EncodedBytes, s => sourceEntity.Get<IblEnvironmentResource>().Data.Write(s));
                if (environmentId != sourceId)
                    Write(environmentId, IblEnvironmentAsset.EncodedBytes, s => environment.Get<IblEnvironmentResource>().Data.Write(s));
                Write(captureId, data.EncodedBytes, s => capture.Get<PbrReflectionCaptureResource>().Data.Write(s, cancellationToken));
                // Validate exact owned files and the bytes actually written, including output decorators and late cancellation.
                OfflineDirectory.RequireFiles(stage, owned, cancellationToken);
                void VerifyEnvironment(AssetId id, string expected)
                {
                    using var stream = File.OpenRead(Path.Combine(stage, id.Value.ToString("N") + ".siaasset"));
                    if (Convert.ToHexString(SHA256.HashData(stream)) != expected) throw new InvalidDataException("Capture environment publication hash differs.");
                    stream.Position = 0; _ = IblEnvironmentRecord.Load(stream); cancellationToken.ThrowIfCancellationRequested();
                }
                VerifyEnvironment(sourceId, sourceHash); VerifyEnvironment(environmentId, environmentHash);
                using (var stream = File.OpenRead(Path.Combine(stage, captureId.Value.ToString("N") + ".siaasset")))
                    if (!data.SameRevision(PbrReflectionCaptureRecord.Load(stream).Data)) throw new InvalidDataException("Capture metadata publication differs.");
                var result = new Result(captureId, owned.Select(Path.GetFileName).Cast<string>().ToArray(), encodedBytes);
                OfflineDirectory.Publish(stage, output, cancellationToken); return result;
            }
            catch (Exception error) {
                try { RequireDirectory(stage); }
                catch (Exception cleanup) { throw new AggregateException("Capture failed; unverifiable staging was preserved.", error, cleanup); }
                var failures = new List<Exception>();
                foreach (var path in owned) {
                    try {
                        if (Path.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                            throw new IOException("Owned capture output became a link; preserved.");
                        File.Delete(path);
                    }
                    catch (Exception cleanup) { failures.Add(cleanup); }
                }
                try { Directory.Delete(stage, recursive: false); } catch (Exception cleanup) { failures.Add(cleanup); }
                if (failures.Count != 0) throw new AggregateException("Capture failed and owned-file cleanup was incomplete.", [error, .. failures]);
                throw;
            }
        }
        finally { acquired.Root.Destroy(); }
    }

    private static void RequireDirectory(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Capture publication directories must not be links.");
    }
}
#endif
