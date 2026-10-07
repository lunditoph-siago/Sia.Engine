#if !BROWSER
using System.Globalization;
using Sia;
using Sia.Engine.Rendering;
using Sia.Engine.Rendering.Pbr;
using Sia.WebGPU;

namespace Sia.Engine.Example;

public static partial class Program
{
    private static (int Minimum, int Maximum) ParseLightmapResolutions(string[] args, int first)
    {
        var minimum = 64; var maximum = 1024;
        var minimumSet = false; var maximumSet = false;
        for (var i = first; i < args.Length; i += 2) {
            if (!int.TryParse(args[i + 1], out var value) || value is < 8 or > 1024 || (value & (value - 1)) != 0)
                throw new ArgumentException("Lightmap receiver resolutions must be powers of two within 8..1024.");
            if (args[i] == "--receiver-resolution" && !minimumSet) { minimum = value; minimumSet = true; }
            else if (args[i] == "--maximum-receiver-resolution" && !maximumSet) { maximum = value; maximumSet = true; }
            else throw new ArgumentException("Expected unique --receiver-resolution/--maximum-receiver-resolution options.");
        }
        if (minimum > maximum) throw new ArgumentException("Minimum receiver resolution exceeds maximum.");
        return (minimum, maximum);
    }

    private static (int Minimum, int Maximum, ulong TraceBytes, int[] Conventional) ParseLightmapStreamOptions(string[] args)
    {
        var resolutions = new List<string>();
        ulong traceBytes = 128ul * 1024 * 1024;
        var traceSet = false;
        int[]? conventional = null;
        for (var i = 4; i < args.Length; i += 2) {
            if (args[i] == "--trace-budget-mib") {
                if (traceSet || !ulong.TryParse(args[i + 1], out var mib) || mib is < 1 or > 4096)
                    throw new ArgumentException("Expected unique --trace-budget-mib 1..4096.");
                traceBytes = mib * 1024 * 1024; traceSet = true;
            }
            else if (args[i] == "--conventional-geometry" && conventional is null)
                conventional = ParseConventionalGeometry(args[i], args[i + 1]);
            else { resolutions.Add(args[i]); resolutions.Add(args[i + 1]); }
        }
        var (minimum, maximum) = ParseLightmapResolutions(resolutions.ToArray(), 0);
        return (minimum, maximum, traceBytes, conventional ?? []);
    }

    // Publish one complete directory so scene, metadata and chunks become visible together.
    private static async Task BakeLightmapStreamAsync(string scenePath, string directory, int resolution, int maximumResolution,
        ulong maximumTraceBytes, int[] conventional)
    {
        var destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var parent = Path.GetDirectoryName(destination);
        if (string.IsNullOrEmpty(parent) || File.Exists(destination) || Directory.Exists(destination))
            throw new IOException("Choose a new lightmap stream output directory.");
        using var client = new HttpClient();
        using var input = await AssetInput.OpenAsync(Path.GetFullPath(scenePath), client);
        var source = await AssetInput.ReadBoundedAsync(input, 1024 * 1024 * 1024);
        var scene = PbrSceneAsset.Decode(source.Span, 1024 * 1024 * 1024);
        if (conventional.Any(id => (uint)id >= (uint)scene.Geometry.Length))
            throw new ArgumentException("Conventional geometry IDs must be source asset indices.");
        source = default;
        var settings = new PbrLightmapBakeSettings {
            Sky = new ProceduralSky { Intensity = .75f },
            TowardLight = Sia.Math.math.normalize(new Sia.Math.float3(-.8f, 1, .4f)),
            LightRadiance = new Sia.Math.float3(1, .96f, .9f) * 3
        };
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, "." + Path.GetFileName(destination) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        if (Directory.Exists(staging) || File.Exists(staging)) throw new IOException("Lightmap staging path already exists.");
        Directory.CreateDirectory(staging);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        long chunkBytes = 0; var chunks = 0;
        try {
            var baked = await PbrLightmapBaker.BakeStreamAsync(scene, async (chunk, bytes, token) => {
                await using var file = new FileStream(Path.Combine(staging, chunk.FileName), FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await file.WriteAsync(bytes, token);
                chunkBytes += bytes.Length; chunks++;
                if (chunks % 128 == 0)
                    Console.WriteLine($"Lightmap bake progress: {chunks} chunks/{chunkBytes} bytes; {watch.Elapsed.TotalSeconds:F1}s.");
            }, settings, resolution, maximumResolution, maximumTraceBytes: maximumTraceBytes);
            scene = null!;
            var sceneBytes = baked.Scene.Encode();
            var sceneByteCount = sceneBytes.Length;
            var metadataByteCount = baked.Metadata.Length;
            var restored = PbrSceneAsset.Decode(sceneBytes, 1024 * 1024 * 1024);
            await using var stream = await PbrLightmapStream.OpenAsync(baked.Metadata, Sia.Asset.AssetChunkSources.Directory(staging));
            if (!stream.SceneIdentity.Span.SequenceEqual(PbrLightmapAsset.Identity(restored)))
                throw new InvalidDataException("Mapped scene and lightmap stream identities differ.");
            restored = null!;
            // Validate every emitted page from its actual file before publication; opening alone does no fine IO.
            for (var page = 0; page < stream.Pages.Length; page++) _ = await stream.ReadPageAsync(page);
            await File.WriteAllBytesAsync(Path.Combine(staging, "scene.siapbr"), sceneBytes);
            await File.WriteAllBytesAsync(Path.Combine(staging, "lightmaps.sialmst"), baked.Metadata);
            sceneBytes = null!;
            await stream.DisposeAsync();
            var mixed = await PbrSceneStream.CookAsync(baked.Scene, async (chunk, bytes, token) => {
                await using var file = new FileStream(Path.Combine(staging, chunk.FileName), FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await file.WriteAsync(bytes, token);
            }, conventional);
            await using (var check = await PbrSceneStream.OpenAsync(mixed, Sia.Asset.AssetChunkSources.Directory(staging))) {
                if (!check.LightmapIdentity.Span.SequenceEqual(stream.SceneIdentity.Span)
                    || !check.StaticIdentity.Span.SequenceEqual(PbrSceneTransport.StaticIdentity(baked.Scene)))
                    throw new InvalidDataException("Mixed stream changed the mapped bake domain.");
            }
            await File.WriteAllBytesAsync(Path.Combine(staging, "Bistro.siastream"), mixed);
            mixed = null!;
            baked = default;
            // Moving, transparent and uncovered receivers need the same static diffuse seed in every tier.
            // Reuse the existing GPU bake; publish it atomically with the matching mapped scene and pages.
            BakeProbes(Path.Combine(staging, "scene.siapbr"), Path.Combine(staging, "probes.siaprobe"));
            BakeEnvironment(settings.Sky.Intensity.ToString(CultureInfo.InvariantCulture), Path.Combine(staging, "environment.siaenv"));
            var inputs = new SceneInputs("scene.siapbr", "Bistro.siastream", "environment.siaenv", "lightmaps.sialmst", "probes.siaprobe");
            await File.WriteAllBytesAsync(Path.Combine(staging, "scene.siascene"), inputs.Encode());
            Directory.Move(staging, destination);
            Console.WriteLine($"Paged lightmaps baked: {stream.Receivers.Length} receivers; {stream.Resolution} virtual square; {stream.Pages.Length} pages; {chunks} chunks/{chunkBytes} bytes; metadata {metadataByteCount} bytes; mapped scene {sceneByteCount} bytes; {watch.Elapsed.TotalSeconds:F3}s; {destination}");
        }
        catch {
            // This uniquely named staging directory was created by this invocation; final destination is never removed.
            if (!string.Equals(Path.GetDirectoryName(staging), parent, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Lightmap staging cleanup escaped its output parent.");
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            throw;
        }
    }

    // Writes a validated matching scene/lightmap pair to new files; never overwrites source assets.
    private static void BakeLightmaps(string scenePath, string output, string mappedScenePath, int resolution, int maximumResolution)
    {
        var sourcePath = Path.GetFullPath(scenePath);
        var lightmapPath = Path.GetFullPath(output);
        var mappedPath = Path.GetFullPath(mappedScenePath);
        if (string.Equals(sourcePath, lightmapPath, StringComparison.OrdinalIgnoreCase)
            || string.Equals(sourcePath, mappedPath, StringComparison.OrdinalIgnoreCase)
            || string.Equals(lightmapPath, mappedPath, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Source, mapped scene and lightmap must use different paths.");
        if (File.Exists(lightmapPath) || File.Exists(mappedPath))
            throw new IOException("Lightmap bake outputs must be new files.");
        using var client = new HttpClient();
        using var source = AssetInput.OpenAsync(sourcePath, client).GetAwaiter().GetResult();
        var sourceBytes = AssetInput.ReadBoundedAsync(source, 1024 * 1024 * 1024).GetAwaiter().GetResult();
        var scene = PbrSceneAsset.Decode(sourceBytes.Span, 1024 * 1024 * 1024);
        var settings = new PbrLightmapBakeSettings {
            Sky = new ProceduralSky { Intensity = .75f },
            TowardLight = Sia.Math.math.normalize(new Sia.Math.float3(-.8f, 1, .4f)),
            LightRadiance = new Sia.Math.float3(1, .96f, .9f) * 3
        };
        var lightmaps = PbrLightmapBaker.BakeAdaptive(scene, out var mappedScene, settings, resolution, maximumResolution,
            encoding: PbrLightmapEncoding.L1Unorm8);
        var sceneBytes = mappedScene.Encode();
        var lightmapBytes = lightmaps.Encode();
        var restoredScene = PbrSceneAsset.Decode(sceneBytes, 1024 * 1024 * 1024);
        var restoredLightmaps = PbrLightmapAsset.Decode(lightmapBytes);
        if (!restoredLightmaps.SceneIdentity.Span.SequenceEqual(PbrLightmapAsset.Identity(restoredScene)))
            throw new InvalidDataException("Mapped scene and lightmap roundtrip identities differ.");
        var createdLightmap = false;
        var createdScene = false;
        try {
            using var lightmapFile = new FileStream(lightmapPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            createdLightmap = true;
            using var sceneFile = new FileStream(mappedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            createdScene = true;
            lightmapFile.Write(lightmapBytes);
            sceneFile.Write(sceneBytes);
        }
        catch {
            if (createdLightmap) File.Delete(lightmapPath);
            if (createdScene) File.Delete(mappedPath);
            throw;
        }
        Console.WriteLine($"Surface lightmaps baked: {lightmaps.Receivers.Length} receivers, {lightmaps.Resolution} square, {lightmaps.Encoding}, {lightmapBytes.Length} encoded bytes, {lightmapPath}; mapped scene {mappedPath}");
    }

    private static void BakeProbes(string scenePath, string output)
    {
        using var client = new HttpClient();
        var streaming = scenePath.EndsWith(".siastream", StringComparison.OrdinalIgnoreCase)
            ? OpenStreamAsync(scenePath, client).GetAwaiter().GetResult() : null;
        SceneTraceData tracing;
        try {
            tracing = streaming is null
                ? PbrSceneTransport.BuildStatic(PbrSceneAsset.Decode(File.ReadAllBytes(scenePath), 1024 * 1024 * 1024))
                : streaming.StaticIdentity.IsEmpty ? PbrSceneTransport.Build(streaming) : PbrSceneTransport.BuildStatic(streaming);
        }
        finally { if (streaming is not null) streaming.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        var sky = new ProceduralSky { Intensity = .75f };
        var volume = PbrSceneTransport.CreateVolume(tracing, new(4, 4, 4), sky);
        var instance = Wgpu.CreateInstance();
        WgpuHandle<WGPUAdapter> adapter = default;
        try {
            adapter = Wgpu.RequestAdapter(instance, WGPURequestAdapterOptions.Default);
            using var world = new World();
            var device = world.OwnWgpu(Wgpu.RequestDevice(adapter, WGPUDeviceDescriptor.Default));
            var queue = world.OwnWgpu(Wgpu.GetQueue(device.GetWgpu<WGPUDevice>()));
            var frame = new GpuFrame(world, world, device, queue);
            using var probes = new DiffuseProbeGpu(frame, volume, tracing);
            var towardSun = Sia.Math.math.normalize(new Sia.Math.float3(-.8f, 1, .4f));
            var baked = probes.Bake(instance, sky, towardSun, new Sia.Math.float3(1, .96f, .9f) * 3);
            using var payload = new MemoryStream();
            baked.Write(payload);
            payload.Position = 0;
            _ = DiffuseProbeAsset.Read(payload);
            File.WriteAllBytes(output, payload.ToArray());
            Console.WriteLine($"Scene probes baked: {baked.Count} probes, {tracing.TriangleCount} triangles, {probes.Bytes} requested GPU bytes, {output}");
        }
        finally { Wgpu.Release(ref adapter); Wgpu.Release(ref instance); }
    }

    // --bake-environment INTENSITY --output FILE.siaenv; full sky inputs are available through the baker API.
    private static void BakeEnvironment(string intensity, string output)
    {
        var sky = new ProceduralSky { Intensity = float.Parse(intensity, CultureInfo.InvariantCulture) };
        sky.Validate();
        var instance = Wgpu.CreateInstance();
        WgpuHandle<WGPUAdapter> adapter = default;
        try {
            adapter = Wgpu.RequestAdapter(instance, WGPURequestAdapterOptions.Default);
            using var world = new World();
            var device = world.OwnWgpu(Wgpu.RequestDevice(adapter, WGPUDeviceDescriptor.Default));
            var queue = world.OwnWgpu(Wgpu.GetQueue(device.GetWgpu<WGPUDevice>()));
            var frame = new GpuFrame(world, world, device, queue);
            var asset = IblEnvironmentBaker.Bake(frame, instance, sky);
            var reference = IrradianceSh.Project(sky.Evaluate);
            var error = 0f;
            for (var i = 0; i < 9; i++) {
                var delta = Sia.Math.math.abs(asset.Coefficients.Span[i] - reference[i]);
                error = MathF.Max(error, MathF.Max(delta.x, MathF.Max(delta.y, delta.z)));
            }
            if (error > .002f)
                throw new InvalidDataException($"GPU/CPU SH disagreement: {error}.");
            // Publish only a validated complete payload; leave the destination untouched on bake failure.
            using var payload = new MemoryStream();
            asset.Write(payload);
            payload.Position = 0;
            _ = IblEnvironmentAsset.Read(payload);
            File.WriteAllBytes(output, payload.ToArray());
            Console.WriteLine($"GPU environment baked: {payload.Length} bytes; maximum SH reference error {error:G6}; {output}");
        }
        finally {
            Wgpu.Release(ref adapter);
            Wgpu.Release(ref instance);
        }
    }
}
#endif
