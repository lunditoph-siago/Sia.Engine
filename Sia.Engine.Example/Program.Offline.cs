#if !BROWSER
using System.Globalization;
using Sia;
using Sia.Asset;
using Sia.Engine.Rendering;
using Sia.Engine.Rendering.Pbr;
using Sia.WebGPU;

namespace Sia.Engine.Example;

public static partial class Program
{
    private static async Task<bool> RunOfflineAsync(string[] args)
    {
        switch (args) {
            case ["--package-example-content", var recipe, "--output", var output, ..] when args.Length is 4 or 6 or 8: {
                var (browser, maximumBytes) = ContentPackageOptions(args);
                await ExampleContent.PackAsync(recipe, output, maximumBundleBytes: maximumBytes, browser: browser);
                Console.WriteLine($"Packaged example content: {Path.GetFullPath(output)}");
                return true;
            }
            case ["--cook-content", var recipe, "--output", var output, ..] when args.Length is 4 or 6: {
                var sceneId = await ContentCook.CookAsync(recipe, output, maximumTotalBytes: OfflineTotalBytes(args));
                Console.WriteLine($"Cooked content {sceneId.Value:N}: {Path.GetFullPath(output)}");
                return true;
            }
            case ["--pack-scene", var recipe, "--output", var output]: {
                var sceneId = await ScenePack.PackAsync(recipe, output);
                Console.WriteLine($"Packed scene {sceneId.Value:N}: {Path.GetFullPath(output)}");
                return true;
            }
            case ["--cook-mesh", var recipe, "--output", var output]: {
                var mesh = MeshCook.Cook(recipe, output);
                Console.WriteLine($"Cooked Mesh {mesh.Value:N}: {Path.GetFullPath(output)}");
                return true;
            }
            case ["--bake-environment", var intensity, "--output", var output]:
                BakeEnvironment(intensity, output);
                return true;
            case ["--bake-probes", var scene, "--output", var output]:
                BakeProbes(scene, output);
                return true;
        }
        return false;
    }

    private static (bool Browser, long MaximumBytes) ContentPackageOptions(string[] args)
    {
        var browser = false;
        var maximumBytes = 512L * 1024 * 1024;
        var options = new HashSet<string>(StringComparer.Ordinal);
        for (var at = 4; at < args.Length; at += 2) {
            if (!options.Add(args[at])) throw new ArgumentException("Duplicate content package option.");
            if (args[at] == "--target") {
                browser = args[at + 1] switch {
                    "browser" => true,
                    "native" => false,
                    _ => throw new ArgumentException("Expected --target native|browser.")
                };
            }
            else if (args[at] == "--maximum-total-bytes" && long.TryParse(args[at + 1],
                System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var bytes) && bytes > 0)
                maximumBytes = bytes;
            else throw new ArgumentException("Expected --target native|browser or --maximum-total-bytes <positive integer>.");
        }
        return (browser, maximumBytes);
    }

    private static long OfflineTotalBytes(string[] args)
    {
        if (args.Length == 4) return 512L * 1024 * 1024;
        if (args[4] != "--maximum-total-bytes" || !long.TryParse(args[5],
            System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var bytes) || bytes <= 0)
            throw new ArgumentException("Expected --maximum-total-bytes <positive integer>.");
        return bytes;
    }

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

    private static void BakeProbes(string scenePath, string output)
    {
        using var client = new HttpClient();
        var tracing = PrepareReferenceProbeTraceAsync(scenePath, client).GetAwaiter().GetResult();
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
