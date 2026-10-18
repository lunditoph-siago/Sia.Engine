#if !BROWSER
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Sia;
using Sia.Asset;
using Sia.Engine.Mesh;
using Sia.Engine.Rendering.Pbr;

namespace Sia.Engine.Example;

// Application-owned bake/publication composition. Asset core owns typed acquisition and lifetime.
internal static partial class LightmapCook
{
    internal readonly record struct Result(AssetId Id, IReadOnlyList<string> Files, ulong StagingBytes, ulong WorkingBytes, ulong EncodedBytes);
    private readonly record struct Receiver(PbrMeshInstance Instance, MeshBakeLayout Layout, int Slot,
        AssetId Product, int ChartCount, int PageCount, int FirstMip);

    internal static Result Cook(PbrSceneFiles.Prepared<MeshBvhRecord> input, ReadOnlySpan<Guid> staticInstances,
        IReadOnlyDictionary<AssetId, MeshBakeLayout> layouts, AssetId productId, string outputDirectory,
        PbrLightmapBakeSettings? settings = null, ulong maximumMetadataBytes = PbrLightmapData.MaximumEncodedBytes,
        ulong maximumWorkingBytes = 64ul * 1024 * 1024, ulong maximumStagingBytes = 512ul * 1024 * 1024,
        CancellationToken cancellationToken = default, Func<string, Stream, Stream>? decorateOutput = null,
        ulong maximumEncodedBytes = ulong.MaxValue, ulong maximumFileBytes = ulong.MaxValue,
        int maximumPageResolution = 64)
    {
        ArgumentNullException.ThrowIfNull(input); ArgumentNullException.ThrowIfNull(layouts);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        cancellationToken.ThrowIfCancellationRequested();
        if (maximumPageResolution is < 2 or > 8192 || (maximumPageResolution & (maximumPageResolution - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(maximumPageResolution));
        if (!productId.IsValid) throw new ArgumentException("Lightmap product identity must be nonempty.", nameof(productId));
        var output = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputDirectory));
        var parent = Path.GetDirectoryName(output);
        if (parent is null || !Directory.Exists(parent)) throw new DirectoryNotFoundException("Lightmap output requires an existing parent.");
        RequireDirectory(parent);
        if (Path.Exists(output)) throw new IOException("Lightmap output must not already exist.");

        // Select before acquisition: unused/dynamic bindings neither enter transport identity nor become dependencies.
        var selected = Select(input, staticInstances, cancellationToken);
        var claims = Claims(selected);
        if (claims.ContainsKey(productId)) throw new InvalidDataException("Lightmap output aliases an input asset identity.");
        claims.Add(productId,typeof(PbrLightmapSetRecord));
        using var world = new World(); world.AddAddon<AssetLibrary>();
        var acquired = PbrSceneFiles.AcquireBvh(world, selected);
        AssetLibrary.RegisterAsset<MeshSourceAsset, MeshSourceRecord>();
        AssetLibrary.RegisterAsset<PbrLightmapResource, PbrLightmapRecord>();
        AssetLibrary.RegisterAsset<PbrLightmapSetResource, PbrLightmapSetRecord>();
        AssetLibrary.RegisterAsset<PbrLightmapPageResource, PbrLightmapPageRecord>();
        try {
            var receivers = new List<Receiver>(); var pageCount = 0;
            ulong encodedProducts = 0, largestRaw = 0, largestMetadata=0; var largestPages = 0; var largestCharts = 0;
            foreach (var instance in selected.Scene.Data.Instances) {
                cancellationToken.ThrowIfCancellationRequested();
                var binding = selected.Scene.Data.Bindings[instance.Binding];
                var mesh = selected.Meshes[binding.Mesh.Value].Data;
                if (!layouts.TryGetValue(binding.Mesh.Value, out var layout) || !mesh.HasBakeUvs || layout.Source.ContentHash != mesh.SourceHash)
                    throw new InvalidDataException("Static receivers require the exact canonical layout returned by MeshCook.");
                var source = world.AcquireAsset(mesh.Source, () => new MeshSourceRecord { Source = layout.Source }, acquired.Root).Get<MeshSourceAsset>().Source;
                if (source.ContentHash != mesh.SourceHash) throw new InvalidDataException("Canonical receiver source differs from its manifest.");
                foreach (var material in binding.Materials) {
                    var count = 0;
                    foreach (var chart in layout.Charts) if (chart.MaterialSlot == material.Slot) count++;
                    // Degenerate-only slots cannot receive a bake; their live shading remains a caller decision.
                    if (count == 0) continue;
                    var firstMip = 0;
                    while ((layout.Resolution >> firstMip) > maximumPageResolution) firstMip++;
                    var tiles = CountTiles(layout.Resolution >> firstMip, out var rawBytes, out var encodedBytes);
                    var member=PbrLightmapSetData.ReceiverProductId(productId,instance.Id,material.Slot);
                    if (!claims.TryAdd(member,typeof(PbrLightmapRecord))) throw new InvalidDataException("Lightmap receiver product aliases an asset identity.");
                    var metadataBytes=PbrLightmapData.RequiredBytes(1,count,tiles);
                    var memberBytes=356ul+(ulong)count*48+(ulong)tiles*68;
                    if (memberBytes>maximumFileBytes) throw new InvalidDataException("Lightmap receiver metadata exceeds per-file admission.");
                    receivers.Add(new(instance,layout,material.Slot,member,count,tiles,firstMip)); pageCount=checked(pageCount+tiles);
                    _=PbrLightmapSetData.RequiredBytes(receivers.Count,receivers.Count);
                    foreach (var tile in Tiles(layout.Resolution,firstMip)) {
                        var id=PbrLightmapData.PageId(member,instance.Id,material.Slot,tile.Level,tile.X,tile.Y);
                        if (!claims.TryAdd(id,typeof(PbrLightmapPageRecord))) throw new InvalidDataException("Lightmap page aliases an asset identity.");
                    }
                    largestMetadata=System.Math.Max(largestMetadata,metadataBytes);
                    encodedProducts=checked(encodedProducts+encodedBytes+memberBytes); largestRaw=System.Math.Max(largestRaw,rawBytes);
                    largestPages = System.Math.Max(largestPages, tiles); largestCharts = System.Math.Max(largestCharts, layout.Charts.Length);
                }
            }
            var rootMetadata=PbrLightmapSetData.RequiredBytes(receivers.Count,receivers.Count);
            var metadata=checked(rootMetadata+largestMetadata);
            if (metadata > maximumMetadataBytes) throw new ArgumentException("Lightmap manifest exceeds its metadata budget.", nameof(maximumMetadataBytes));
            var indexBytes=232ul+(ulong)receivers.Count*236;
            var outputBytes=checked(encodedProducts+indexBytes);
            if (outputBytes > maximumEncodedBytes || indexBytes > maximumFileBytes || receivers.Any(receiver =>
                92ul + (ulong)System.Math.Min(64, receiver.Layout.Resolution >> receiver.FirstMip) * (ulong)System.Math.Min(64, receiver.Layout.Resolution >> receiver.FirstMip) * 25 > maximumFileBytes))
                throw new InvalidDataException("Lightmap output exceeds selected encoded publication admission.");
            var stagingBytes=checked(outputBytes+largestRaw);
            if (stagingBytes > maximumStagingBytes) throw new ArgumentException("Lightmap publication exceeds its staging byte budget.", nameof(maximumStagingBytes));
            // Declared payload/scratch reservation, not a process-memory cap. Borrowed inputs,
            // object headers, World/receipt bookkeeping and temporary validation sets are excluded.
            // Reserve both metadata snapshots, both open file buffers and one encoded page/hash.
            var fixedBytes = checked(2 * metadata + (ulong)receivers.Capacity * (ulong)Unsafe.SizeOf<Receiver>()
                + (ulong)largestPages * sizeof(long) + (ulong)largestCharts * (ulong)Unsafe.SizeOf<PbrLightmapCoefficients>()
                + 4096ul * (ulong)Unsafe.SizeOf<PbrLightmapCoefficients>() + 12544 + 32768
                + PbrLightmapPageData.MaximumEncodedBytes + 2 * 4096 + 128);
            ulong Remaining(ulong used) => used <= maximumWorkingBytes ? maximumWorkingBytes - used
                : throw new ArgumentException("Lightmap known working arrays exceed their budget.", nameof(maximumWorkingBytes));
            var trace = PbrBvhTrace.Build(acquired.Scene, Remaining(fixedBytes), cancellationToken);
            var baker = new PbrLightmapBaker(trace, settings, Remaining(fixedBytes + trace.PayloadBytes), cancellationToken);
            var baseBytes = fixedBytes + trace.PayloadBytes + baker.PayloadBytes;
            var workingBytes = baseBytes;
            foreach (var receiver in receivers) {
                var surface = new PbrLightmapSurface(receiver.Layout, receiver.Instance.Transform, receiver.Slot, Remaining(baseBytes), cancellationToken);
                var producer = new PbrLightmapBake(surface, baker, Remaining(baseBytes + surface.PayloadBytes));
                workingBytes = System.Math.Max(workingBytes, baseBytes + surface.PayloadBytes + producer.PayloadBytes);
            }

            var references=new PbrLightmapProductReference[receivers.Count];
            var bindings=new PbrLightmapReceiverReference[receivers.Count];
            var actualSettings = settings ?? new();
            var sceneIdentity = Convert.ToHexString(acquired.Scene.TransportIdentity.Span);
            var stage = Path.Combine(parent, ".lightmap-cook-" + Guid.NewGuid().ToString("N"));
            if (Path.Exists(stage)) throw new IOException("Lightmap staging path already exists.");
            var owned = new List<string>(pageCount + receivers.Count + 2);
            cancellationToken.ThrowIfCancellationRequested(); Directory.CreateDirectory(stage);
            FileStream CreateFile(string filename)
            {
                cancellationToken.ThrowIfCancellationRequested(); RequireDirectory(stage);
                var path = Path.Combine(stage, filename);
                var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
                owned.Add(path); return file;
            }
            Stream Decorate(FileStream file, ulong limit) => decorateOutput?.Invoke(file.Name, new OutputStream(file, limit, cancellationToken))
                ?? new OutputStream(file, limit, cancellationToken);
            void Write(string filename, ulong bytes, Action<Stream> encode)
            {
                using var file = CreateFile(filename); using var stream = Decorate(file, bytes);
                encode(stream); stream.Flush();
                if ((ulong)file.Length != bytes) throw new InvalidDataException("Lightmap output differs from its admitted encoded size.");
            }
            try {
                var raw = new PbrLightmapCoefficients[4096];
                for (var at = 0; at < receivers.Count; at++) {
                    cancellationToken.ThrowIfCancellationRequested(); var receiver = receivers[at];
                    var surface = new PbrLightmapSurface(receiver.Layout, receiver.Instance.Transform, receiver.Slot, Remaining(baseBytes), cancellationToken);
                    var producer = new PbrLightmapBake(surface, baker, Remaining(baseBytes + surface.PayloadBytes));
                    var descriptors=new PbrLightmapPageDescriptor[receiver.PageCount]; var pageAt=0;
                    foreach (var tile in Tiles(receiver.Layout.Resolution,receiver.FirstMip)) {
                        var id=PbrLightmapData.PageId(receiver.Product,receiver.Instance.Id,receiver.Slot,tile.Level,tile.X,tile.Y);
                        descriptors[pageAt++]=new(id,string.Empty,0,tile.Level,tile.X,tile.Y,tile.Edge);
                    }
                    var coarse = new PbrLightmapCoefficients[receiver.Layout.Charts.Length];
                    var offsets = new long[receiver.PageCount]; Array.Fill(offsets, -1);
                    var spoolPath = Path.Combine(stage, ".receiver.raw"); PbrLightmapQuantization quantization; PbrLightmapCoefficients support;
                    using (var file = CreateFile(".receiver.raw")) {
                        _ = CountTiles(receiver.Layout.Resolution >> receiver.FirstMip, out var expectedRaw, out _);
                        using (var spool = Decorate(file, expectedRaw)) {
                            quantization = producer.Run((tile, values, token) => {
                                var slot = TileSlot(receiver.Layout.Resolution, receiver.FirstMip, tile);
                                if (offsets[slot] != -1) throw new InvalidDataException("Duplicate lightmap producer tile.");
                                offsets[slot] = file.Position; WriteRaw(spool, values, token);
                            }, coarse,out support,cancellationToken,firstMip:receiver.FirstMip);
                            spool.Flush();
                            if ((ulong)file.Length != expectedRaw) throw new InvalidDataException("Incomplete lightmap receiver staging.");
                            for (var local = 0; local < receiver.PageCount; local++) {
                                cancellationToken.ThrowIfCancellationRequested();
                                if (offsets[local] < 0) throw new InvalidDataException("Missing lightmap producer tile.");
                                var descriptor = descriptors[local];
                                file.Position = offsets[local]; ReadRaw(file, raw.AsSpan(0, descriptor.Edge * descriptor.Edge), cancellationToken);
                                var page = PbrLightmapPageData.Create(descriptor.Edge, raw.AsSpan(0, descriptor.Edge * descriptor.Edge),
                                    cancellationToken: cancellationToken, quantization: quantization);
                                var entity = world.AcquireAsset(descriptor.Id, () => new PbrLightmapPageRecord { Data = page });
                                try { Write(descriptor.Id.Value.ToString("N") + ".siaasset", (ulong)page.EncodedBytes, s => entity.Get<PbrLightmapPageResource>().Data.Save(s, cancellationToken)); }
                                finally { entity.Destroy(); }
                                descriptors[local] = descriptor with { ContentHash = page.ContentHash };
                            }
                        }
                    }
                    DeleteFile(spoolPath); owned.Remove(spoolPath);
                    var record=new PbrLightmapReceiver(receiver.Instance.Id,receiver.Slot,receiver.Layout.Source.ContentHash,receiver.Layout.Resolution,
                        receiver.Layout.Padding,0,receiver.ChartCount,quantization,receiver.FirstMip);
                    var charts=new PbrLightmapChart[receiver.ChartCount]; var chartAt=0;
                    for (var sourceChart = 0; sourceChart < receiver.Layout.Charts.Length; sourceChart++) {
                        var bounds = receiver.Layout.Charts[sourceChart]; if (bounds.MaterialSlot != receiver.Slot) continue;
                        charts[chartAt++] = new(0,bounds.X,bounds.Y,bounds.Width,bounds.Height,coarse[sourceChart]);
                    }
                    var member=PbrLightmapData.Create(receiver.Product,sceneIdentity,actualSettings,[record],charts,descriptors,largestMetadata,cancellationToken);
                    var memberEntity=world.AcquireAsset(receiver.Product,()=>new PbrLightmapRecord { Data=member });
                    try { Write(receiver.Product.Value.ToString("N")+".siaasset",member.EncodedBytes,s=>memberEntity.Get<PbrLightmapResource>().Data.Save(s,cancellationToken)); }
                    finally { memberEntity.Destroy(); }
                    RequireDirectory(stage); var memberPath=Path.Combine(stage,receiver.Product.Value.ToString("N")+".siaasset");
                    if ((File.GetAttributes(memberPath)&FileAttributes.ReparsePoint)!=0) throw new IOException("Receiver metadata became a link.");
                    using (var file=File.OpenRead(memberPath)) {
                        var contentHash=Convert.ToHexString(SHA256.HashData(file));
                        references[at]=new(receiver.Product,contentHash,checked((int)member.EncodedBytes),member.BakeIdentity);
                    }
                    bindings[at]=new(record,at,0,support);
                }
                var data=PbrLightmapSetData.Create(productId,sceneIdentity,actualSettings,references,bindings,rootMetadata,cancellationToken);
                var root=world.AcquireAsset(productId,()=>new PbrLightmapSetRecord { Data=data });
                try { Write(productId.Value.ToString("N") + ".siaasset",data.EncodedBytes,s=>root.Get<PbrLightmapSetResource>().Data.Save(s,cancellationToken)); }
                finally { root.Destroy(); }
                var result = new Result(productId, owned.Select(Path.GetFileName).Cast<string>().ToArray(), stagingBytes, workingBytes, outputBytes);
                OfflineDirectory.RequireFiles(stage, owned, cancellationToken);
                OfflineDirectory.Publish(stage, output, cancellationToken);
                return result;
            }
            catch (Exception error) {
                try { RequireDirectory(stage); }
                catch (Exception cleanup) { throw new AggregateException("Lightmap publication failed; unverifiable staging was preserved.", error, cleanup); }
                var failures = new List<Exception>();
                foreach (var path in owned) { try { DeleteFile(path); } catch (Exception cleanup) { failures.Add(cleanup); } }
                try { Directory.Delete(stage, recursive: false); } catch (Exception cleanup) { failures.Add(cleanup); }
                if (failures.Count != 0) throw new AggregateException("Lightmap publication failed and owned-file cleanup was incomplete.", [error, .. failures]);
                throw;
            }
        }
        finally { acquired.Root.Destroy(); }
    }

    private static PbrSceneFiles.Prepared<MeshBvhRecord> Select(PbrSceneFiles.Prepared<MeshBvhRecord> input, ReadOnlySpan<Guid> ids, CancellationToken token)
    {
        var scene = input.Scene.Data.SelectInstances(ids, token);
        var meshes = new Dictionary<AssetId, MeshManifestRecord>(); var products = new Dictionary<AssetId, MeshBvhRecord>();
        var materials = new Dictionary<AssetId, PbrMaterialRecord>(); var textures = new Dictionary<AssetId, PbrTextureRecord>();
        foreach (var binding in scene.Bindings) {
            token.ThrowIfCancellationRequested(); var mesh = input.Meshes[binding.Mesh.Value]; meshes[binding.Mesh.Value] = mesh;
            var bvh = mesh.Data.Bvh ?? throw new InvalidDataException("Static transport requires a selected local BVH.");
            products[bvh.Id] = input.Products[bvh.Id];
            foreach (var slot in binding.Materials) {
                var material = input.Materials[slot.Material.Value]; materials[slot.Material.Value] = material;
                foreach (var texture in material.Data.TextureReferences) textures[texture.Value] = input.Textures[texture.Value];
            }
        }
        return new(input.Id, input.Scene with { Data = scene }, meshes, products, materials, textures);
    }

    private static Dictionary<AssetId, Type> Claims(PbrSceneFiles.Prepared<MeshBvhRecord> input)
    {
        var claims = new Dictionary<AssetId, Type>();
        void Claim(AssetId id, Type type)
        {
            if (!id.IsValid || claims.TryGetValue(id, out var existing) && existing != type)
                throw new InvalidDataException("Static bake input contains a cross-kind logical asset identity.");
            claims[id] = type;
        }
        Claim(input.Id, typeof(PbrSceneRecord));
        foreach (var item in input.Meshes) {
            Claim(item.Key, typeof(MeshManifestRecord)); Claim(item.Value.Data.Source, typeof(MeshSourceRecord));
            if (item.Value.Data.Raster is { } raster) Claim(raster.Id, typeof(MeshRasterRecord));
            if (item.Value.Data.Virtual is { } virtualData) Claim(virtualData.Id, typeof(MeshVirtualRecord));
        }
        foreach (var item in input.Products) Claim(item.Key, typeof(MeshBvhRecord));
        foreach (var item in input.Materials) Claim(item.Key, typeof(PbrMaterialRecord));
        foreach (var item in input.Textures) Claim(item.Key, typeof(PbrTextureRecord));
        return claims;
    }

    private static void RequireDirectory(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Lightmap publication directories must not be links.");
    }

    private static void DeleteFile(string path)
    {
        if (Path.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Owned lightmap output became a link; preserved for inspection.");
        File.Delete(path);
    }
}
#endif
