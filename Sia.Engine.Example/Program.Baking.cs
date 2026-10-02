#if !BROWSER
using System.Globalization;
using Sia;
using Sia.Engine.Rendering;
using Sia.Engine.Rendering.Pbr;
using Sia.WebGPU;

namespace Sia.Engine.Example;

public static partial class Program
{
    private static void BakeProbes(string scenePath, string output)
    {
        using var client = new HttpClient();
        var streaming = scenePath.EndsWith(".siastream", StringComparison.OrdinalIgnoreCase)
            ? OpenStreamAsync(scenePath, client).GetAwaiter().GetResult() : null;
        SceneTraceData tracing;
        try {
            tracing = streaming is null
                ? PbrSceneTransport.Build(PbrSceneAsset.Decode(File.ReadAllBytes(scenePath), 1024 * 1024 * 1024))
                : PbrSceneTransport.Build(streaming);
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
