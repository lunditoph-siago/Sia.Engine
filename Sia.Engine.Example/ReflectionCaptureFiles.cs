using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Sia;
using Sia.Asset;
using Sia.Engine.Mesh;
using Sia.Engine.Rendering;
using Sia.Engine.Rendering.Pbr;

namespace Sia.Engine.Example;

// Location, content preparation and synchronous World composition belong to the application.
internal static class ReflectionCaptureFiles
{
    internal sealed record Prepared(PbrReflectionCaptureRecord Capture,
        IblEnvironmentRecord Source, IblEnvironmentRecord Environment);

    internal static async Task<Prepared> PrepareAsync(string path, HttpClient http, PbrSceneFiles.Prepared scene,
        ulong maximumMetadataBytes = 1024ul * 1024, long maximumEncodedBytes = 4L * 1024 * 1024,
        long cacheBytes = 4L * 1024 * 1024, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path); ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(scene); cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfNegative(maximumEncodedBytes);
        if (cacheBytes < IblEnvironmentAsset.EncodedBytes + 1L)
            throw new ArgumentException("Capture environment cannot fit the encoded cache admission.", nameof(cacheBytes));
        var remote = AssetInput.TryGetHttpUri(path, out var uri);
        var filename = remote ? uri!.AbsolutePath : path;
        if (!filename.EndsWith(".siaasset", StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParseExact(Path.GetFileNameWithoutExtension(filename), "N", out var id) || id == Guid.Empty)
            throw new ArgumentException("Capture requires its nonempty UUID and .siaasset suffix.", nameof(path));
        ReadOnlyMemory<byte> bytes;
        using (var stream = await AssetInput.OpenAsync(path, http, cancellationToken))
            bytes = await AssetInput.ReadBoundedAsync(stream, (int)System.Math.Min((ulong)System.Math.Min(maximumEncodedBytes,
                PbrReflectionCaptureData.MaximumEncodedBytes), maximumMetadataBytes), cancellationToken);
        PbrReflectionCaptureRecord Decode(CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); cancellationToken.ThrowIfCancellationRequested();
            if (!MemoryMarshal.TryGetArray(bytes, out var encoded)) throw new InvalidOperationException("Expected array-backed capture input.");
            using var input = new MemoryStream(encoded.Array!, encoded.Offset, encoded.Count, writable: false);
            return PbrReflectionCaptureRecord.Load(input, maximumMetadataBytes, path);
        }
#if BROWSER
        var capture = await Program.BrowserOwner.RunCpuAsync(Decode, cancellationToken);
#else
        var capture = Decode(cancellationToken);
#endif
        var data = capture.Data;
        if (data.ProductId.Value != id || data.Scene.Value != scene.Id)
            throw new InvalidAssetException("Capture logical ID or scene dependency differs from the selected input.");
        Validate(data, scene, cancellationToken);
        var claims = SceneIds(scene);
        if (claims.Contains(data.ProductId) || claims.Contains(data.SourceEnvironment.Id) || claims.Contains(data.Environment.Id))
            throw new InvalidAssetException("Capture or environment aliases a selected scene asset.");
        var routes = new Dictionary<string, AssetId>(StringComparer.Ordinal) { [data.SourceEnvironment.ContentHash] = data.SourceEnvironment.Id };
        routes.TryAdd(data.Environment.ContentHash, data.Environment.Id);
        if ((long)bytes.Length + (long)routes.Count * IblEnvironmentAsset.EncodedBytes > maximumEncodedBytes)
            throw new InvalidDataException("Capture dependencies exceed aggregate encoded admission.");
        string Location(AssetId asset)
        {
            var name = asset.Value.ToString("N") + ".siaasset";
            // Relative resolution drops a query; carry caller transport credentials/options to each sibling.
            return remote ? new UriBuilder(new Uri(uri!, name)) { Query = uri!.Query.TrimStart('?') }.Uri.AbsoluteUri
                : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, name);
        }
        async ValueTask<Stream> Open(AssetChunk chunk, CancellationToken token)
            => chunk.Length == IblEnvironmentAsset.EncodedBytes && routes.TryGetValue(chunk.Id, out var asset)
                ? await AssetInput.OpenAsync(Location(asset), http, token).ConfigureAwait(false)
                : throw new AssetNotFoundException("Capture content is not a declared environment dependency.");
        await using var cache = new AssetChunkCache(Open, cacheBytes, maximumReadAttempts: 1);
        var source = await ChunkLoader.LoadAsync(data.SourceEnvironment.Content, cache,
            maximumBytes: IblEnvironmentAsset.EncodedBytes, cancellationToken: cancellationToken);
        var environment = data.Environment.ContentHash == data.SourceEnvironment.ContentHash
            ? data.Environment.Id == data.SourceEnvironment.Id ? source : source with { Name = Location(data.Environment.Id) }
            : await ChunkLoader.LoadAsync(data.Environment.Content, cache,
                maximumBytes: IblEnvironmentAsset.EncodedBytes, cancellationToken: cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateSharedData(source.Data, environment.Data);
        return new(capture, source, environment);
    }

    internal static void Validate(PbrReflectionCaptureData data, PbrSceneFiles.Prepared scene, CancellationToken token)
    {
        if (data.Scene.Value != scene.Id) throw new InvalidAssetException("Capture refers to another scene.");
        data.ValidateScene(scene.Scene.Data, reference => scene.Meshes[reference.Value].Data,
            reference => scene.Materials[reference.Value].Data, reference => scene.Textures[reference.Value].Data, token);
    }

    internal static HashSet<AssetId> SceneIds(PbrSceneFiles.Prepared scene)
    {
        var ids = new HashSet<AssetId>(scene.Meshes.Keys.Concat(scene.Materials.Keys).Concat(scene.Textures.Keys).Append(scene.Id));
        foreach (var mesh in scene.Meshes.Values) {
            ids.Add(mesh.Data.Source);
            if (mesh.Data.Raster is { } raster) ids.Add(raster.Id);
            if (mesh.Data.Virtual is { } virtualData) ids.Add(virtualData.Id);
            if (mesh.Data.Bvh is { } bvh) ids.Add(bvh.Id);
        }
        var virtuals = scene switch {
            PbrSceneFiles.PreparedMixed mixed => mixed.Virtuals.Values,
            PbrSceneFiles.PreparedStreaming streaming => streaming.Index.Products.Values,
            PbrSceneFiles.Prepared<MeshVirtualRecord> prepared => prepared.Products.Values,
            _ => Enumerable.Empty<MeshVirtualRecord>()
        };
        foreach (var product in virtuals) foreach (var page in product.Data.Pages) ids.Add(page.Id);
        return ids;
    }

    // The returned root owns exactly this use. Destroy it after renderer disposal or on initialization failure.
    internal static (Entity Root, PbrReflectionCaptureData Capture, IblEnvironmentAsset Environment, PbrReflectionCaptureInput Input)
        Acquire(World world, Prepared prepared)
    {
        ArgumentNullException.ThrowIfNull(world); ArgumentNullException.ThrowIfNull(prepared);
        var library = world.GetAddon<AssetLibrary>() ?? throw new InvalidAssetException("Capture requires the original asset library.");
        var data = prepared.Capture.Data;
        var scene = data.Scene.Find(world)?.Get<PbrSceneResource>().Data ?? throw new AssetNotFoundException("Capture scene is not acquired.");
        data.ValidateScene(scene, r => r.Find(world)?.Get<MeshManifestAsset>().Data ?? throw new AssetNotFoundException(),
            r => r.Find(world)?.Get<PbrMaterialResource>().Data ?? throw new AssetNotFoundException(),
            r => r.Find(world)?.Get<PbrTextureAsset>().Data ?? throw new AssetNotFoundException());
        AssetLibrary.RegisterAsset<PbrReflectionCaptureResource, PbrReflectionCaptureRecord>();
        AssetLibrary.RegisterAsset<IblEnvironmentResource, IblEnvironmentRecord>();
        var captureRef = new AssetRefer<PbrReflectionCaptureRecord>.Id(data.ProductId);
        if (captureRef.Find(world) is { } existing && !data.SameRevision(existing.Get<PbrReflectionCaptureResource>().Data))
            throw new InvalidAssetException("A different capture revision is already acquired under this ID.");
        ValidateEnvironment(data.SourceEnvironment, prepared.Source);
        ValidateEnvironment(data.Environment, prepared.Environment);
        ValidateSharedData(prepared.Source.Data, prepared.Environment.Data);
        var root = RenderAssetUse.Create(world, data.Scene);
        try {
            world.AcquireAsset<PbrSceneRecord>(data.Scene.Value, () => throw new AssetNotFoundException(), root);
            world.AcquireAsset(data.ProductId, () => prepared.Capture, root);
            var source = world.AcquireAsset(data.SourceEnvironment.Id, () => prepared.Source, root);
            var environment = world.AcquireAsset(data.Environment.Id, () => data.Environment.Id != data.SourceEnvironment.Id
                && ReferenceEquals(prepared.Environment, prepared.Source) ? prepared.Environment with { } : prepared.Environment, root);
            var value = environment.Get<IblEnvironmentResource>().Data;
            var input = PbrReflectionCaptureInput.Create(data, scene,
                r => r.Find(world)?.Get<MeshManifestAsset>().Data ?? throw new AssetNotFoundException(),
                r => r.Find(world)?.Get<PbrMaterialResource>().Data ?? throw new AssetNotFoundException(),
                r => r.Find(world)?.Get<PbrTextureAsset>().Data ?? throw new AssetNotFoundException(),
                source.Get<IblEnvironmentResource>().Data, value);
            return (root, data, value, input);
        }
        catch { root.Destroy(); throw; }

        void ValidateEnvironment(PbrReflectionEnvironmentReference reference, IblEnvironmentRecord record)
        {
            var entity = reference.Reference.Find(world);
            if (entity is null) {
                if (library.Entities.Values.Any(item => item.Get<AssetMetadata>().Id == reference.Id))
                    throw new InvalidAssetException("Capture environment aliases another acquired record kind.");
                if (EnvironmentHash(record.Data) != reference.ContentHash)
                    throw new InvalidAssetException("Prepared capture environment differs from its declared content.");
                return;
            }
            if (Unsafe.IsNullRef(ref entity.Value.GetOrNullRef<IblEnvironmentResource>())
                || EnvironmentHash(entity.Value.Get<IblEnvironmentResource>().Data) != reference.ContentHash)
                throw new InvalidAssetException("A different environment revision/kind is already acquired under this ID.");
            if (EnvironmentHash(record.Data) != reference.ContentHash)
                throw new InvalidAssetException("Prepared capture environment differs from its declared content.");
        }
    }

    internal static string EnvironmentHash(IblEnvironmentAsset data)
        => data.Write(Stream.Null);

    private static void ValidateSharedData(IblEnvironmentAsset source, IblEnvironmentAsset environment)
    {
        if (source.Sky != environment.Sky || !source.Coefficients.Span.SequenceEqual(environment.Coefficients.Span)
            || !source.BrdfLut.Span.SequenceEqual(environment.BrdfLut.Span))
            throw new InvalidAssetException("Capture must retain its source sky, diffuse SH and BRDF data.");
    }

}
