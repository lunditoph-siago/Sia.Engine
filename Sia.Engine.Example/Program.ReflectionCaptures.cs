using System.Globalization;
using Sia.Engine.Rendering.Pbr;
using Sia.Math;

namespace Sia.Engine.Example;

public static partial class Program
{
    internal static Task<PbrReflectionCaptureAsset> LoadReflectionCaptureAsync(string path, HttpClient http)
        => AssetInput.LoadExactAsync(path, http, PbrReflectionCaptureAsset.EncodedBytes, PbrReflectionCaptureAsset.Read);

#if !BROWSER
    internal static async Task BakeReflectionCaptureAsync(string[] args)
    {
        if (args.Length is not (8 or 10) || args[0] != "--bake-reflections")
            throw new ArgumentException("Expected --bake-reflections SCENE --environment ENV --output FILE --capture-region x,y,z,minX,minY,minZ,maxX,maxY,maxZ [--trace-budget-mib 1..4096].");
        string? environmentPath = null, output = null, region = null;
        ulong traceBytes = 128ul * 1024 * 1024;
        var traceSet = false;
        for (var i = 2; i < args.Length; i += 2) {
            switch (args[i]) {
                case "--environment" when environmentPath is null: environmentPath = args[i + 1]; break;
                case "--output" when output is null: output = args[i + 1]; break;
                case "--capture-region" when region is null: region = args[i + 1]; break;
                case "--trace-budget-mib" when !traceSet:
                    if (!ulong.TryParse(args[i + 1], out var mib) || mib is < 1 or > 4096)
                        throw new ArgumentException("Expected --trace-budget-mib 1..4096.");
                    traceBytes = mib * 1024 * 1024; traceSet = true; break;
                default: throw new ArgumentException("Unknown or duplicate reflection bake option: " + args[i]);
            }
        }
        if (environmentPath is null || output is null || region is null)
            throw new ArgumentException("Reflection baking requires --environment, --output and --capture-region.");
        var values = region.Split(',');
        var coordinates = new float[9];
        if (values.Length != coordinates.Length)
            throw new ArgumentException("Capture region requires nine coordinates: point, minimum and maximum.");
        for (var i = 0; i < values.Length; i++) {
            if (!float.TryParse(values[i], NumberStyles.Float, CultureInfo.InvariantCulture, out coordinates[i])
                || !float.IsFinite(coordinates[i]) || MathF.Abs(coordinates[i]) > 100000)
                throw new ArgumentException("Capture coordinates must be finite and within -100000..100000.");
        }
        var position = new float3(coordinates[0], coordinates[1], coordinates[2]);
        var bounds = new Aabb(new(coordinates[3], coordinates[4], coordinates[5]), new(coordinates[6], coordinates[7], coordinates[8]));
        if (!math.all(bounds.Min < position & position < bounds.Max))
            throw new ArgumentException("Capture point must lie strictly inside its box.");
        var destination = Path.GetFullPath(output);
        if (File.Exists(destination) || Directory.Exists(destination))
            throw new IOException("Choose a new reflection capture output file.");
        var parent = Path.GetDirectoryName(destination)!;
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException("Reflection capture output parent must exist.");
        using var http = new HttpClient();
        using var input = await AssetInput.OpenAsync(args[1], http);
        var sceneBytes = await AssetInput.ReadBoundedAsync(input, 1024 * 1024 * 1024);
        var scene = PbrSceneAsset.Decode(sceneBytes.Span, 1024 * 1024 * 1024);
        sceneBytes = default;
        var environment = await LoadEnvironmentAsync(environmentPath, http);
        var capture = PbrReflectionCaptureBaker.Bake(scene, environment, position, bounds,
            math.normalize(new float3(-.8f, 1, .4f)), new float3(1, .96f, .9f) * 3, maximumTraceBytes: traceBytes);
        using var payload = new MemoryStream(PbrReflectionCaptureAsset.EncodedBytes);
        capture.Write(payload);
        payload.Position = 0;
        _ = PbrReflectionCaptureAsset.Read(payload);
        var staging = Path.Combine(parent, "." + Path.GetFileName(destination) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        var created = false;
        try {
            await using (var file = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                created = true;
                await file.WriteAsync(payload.GetBuffer().AsMemory(0, checked((int)payload.Length)));
            }
            File.Move(staging, destination, overwrite: false);
        }
        finally { if (created && File.Exists(staging)) File.Delete(staging); }
        Console.WriteLine($"Static reflection baked: {payload.Length} bytes; trace cap {traceBytes} bytes; point {position}; box {bounds.Min} .. {bounds.Max}; {destination}");
    }
#endif
}
