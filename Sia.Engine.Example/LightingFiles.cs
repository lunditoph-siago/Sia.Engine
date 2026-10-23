using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using Sia;
using Sia.Asset;
using Sia.Engine.Rendering;
using Sia.Engine.Rendering.Pbr;
using Sia.Engine.Lighting;
using Sia.Math;
using Sia.Reactors;

namespace Sia.Engine.Example;

// Application IO and acquisition composition; Rendering owns the record/value codecs.
internal static class LightingFiles
{
    // Scene policy stays in the application. The World owns successfully published lights.
    internal static Entity[] PublishSceneLights(World world, PbrSceneData? scene, ProceduralSky? sky)
    {
        ArgumentNullException.ThrowIfNull(world);
        var authored = scene is { HasAuthoredLights: true };
        var lights = authored ? scene!.Lights : default;
        var directionals = 0; var locals = 0;
        foreach (var light in lights) {
            if (light.Kind == PbrSceneLightKind.Point && light.CastShadows)
                throw new NotSupportedException("Authored point-light shadows are not supported.");
            if (light.Kind == PbrSceneLightKind.Directional) directionals++;
            else if (math.any(light.Color * light.Intensity != 0)) locals++;
        }
        if (directionals > 4 || locals > 256)
            throw new ArgumentException("Authored scene exceeds four directional or 256 active local lights.");
        var environment = sky ?? new ProceduralSky { Intensity = authored ? 0 : .75f };
        var entities = new Entity[authored ? lights.Length : 4]; var count = 0;
        try {
            if (authored) foreach (var light in lights) {
                var rotation = quaternion.identity;
                if (light.Kind != PbrSceneLightKind.Point) {
                    var ray = math.normalize(light.Direction);
                    rotation = quaternion.LookRotation(-ray, MathF.Abs(ray.y) < .999f ? new float3(0, 1, 0) : new float3(1, 0, 0));
                }
                var color = new LightColor(light.Color, light.Intensity);
                var transform = new GlobalTransform(new AffineTransform(light.Position, rotation));
                entities[count++] = light.Kind switch {
                    PbrSceneLightKind.Directional => Create(new DirectionalLight(), color, transform, light.CastShadows),
                    PbrSceneLightKind.Point => Create(new PointLight(light.Range), color, transform, false),
                    PbrSceneLightKind.Spot => Create(new SpotLight(light.Range, light.InnerAngle, light.OuterAngle), color, transform, light.CastShadows),
                    _ => throw new ArgumentException("Unsupported authored light kind.")
                };
            }
            else {
                var sun = quaternion.LookRotation(math.normalize(new float3(-.8f, 1, .4f)), new(0, 1, 0));
                entities[count++] = Create(new DirectionalLight(), new(new(1, .96f, .9f), 3), new(new AffineTransform(float3.zero, sun)), true);
                entities[count++] = Point(new(3, 4, -4), 40);
                entities[count++] = Point(new(5, 4, -8), 24);
                var spot = quaternion.LookRotation(math.normalize(new float3(0, 1, -.3f)), new(0, 0, 1));
                entities[count++] = Create(new SpotLight(12, .3f, .6f), new(new(1, .9f, .75f), 18), new(new AffineTransform(new(2, 6, -5), spot)), true);
            }
            world.AcquireAddon<EnvironmentLighting>().Sky = environment;
            return entities;
        }
        catch {
            for (var i = count - 1; i >= 0; i--) if (entities[i].IsValid) entities[i].Destroy();
            throw;
        }

        Entity Create<T>(T light, LightColor color, GlobalTransform transform, bool shadow) where T : struct
            => shadow ? world.Create(HList.From(light, new ShadowCaster(), color, transform))
                : world.Create(HList.From(light, color, transform));
        Entity Point(float3 position, float intensity) => world.Create(HList.From(new PointLight(15),
            new LightColor(new(1, .85f, .65f), intensity), new Transform(position, quaternion.identity, new float3(1)),
            GlobalTransform.Identity, new Node<SceneGraph>(null)));
    }

    internal sealed record Prepared(IblEnvironmentRecord? Environment, DiffuseProbeRecord? Probes)
    {
        internal AssetContent<IblEnvironmentRecord>? EnvironmentContent { get; init; }
    }

    internal static async Task<Prepared> PrepareAsync(string? environment, string? probes, HttpClient http,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(http); cancellationToken.ThrowIfCancellationRequested();
        var preparedEnvironment = environment is null ? null
            : await ReadAsync<IblEnvironmentRecord>(environment, http, IblEnvironmentAsset.EncodedBytes, exact: true, cancellationToken);
        const int maximumProbeBytes = 76 + DiffuseProbeAsset.MaximumProbes * 9 * 16 + 32;
        var preparedProbes = probes is null ? null
            : await ReadAsync<DiffuseProbeRecord>(probes, http, maximumProbeBytes, exact: false, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return new(preparedEnvironment, preparedProbes);
    }

    private static async Task<TRecord> ReadAsync<TRecord>(string path, HttpClient http, int maximumBytes,
        bool exact, CancellationToken cancellationToken) where TRecord : ILoadable<TRecord>
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var input = await AssetInput.OpenAsync(path, http, cancellationToken);
        ReadOnlyMemory<byte> bytes = exact ? await AssetInput.ReadExactAsync(input, maximumBytes, cancellationToken)
            : await AssetInput.ReadBoundedAsync(input, maximumBytes, cancellationToken);
        if (!MemoryMarshal.TryGetArray(bytes, out var segment))
            throw new InvalidOperationException("Asset input must return an array-backed payload.");
        TRecord Decode(CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); cancellationToken.ThrowIfCancellationRequested();
            using var payload = new MemoryStream(segment.Array!, segment.Offset, segment.Count, writable: false);
            return TRecord.Load(payload, path);
        }
#if BROWSER
        return await Program.BrowserOwner.RunCpuAsync(Decode, cancellationToken);
#else
        return Decode(cancellationToken);
#endif
    }

    // Synchronous owner-context operation. The caller retains this root until renderers are disposed
    // and destroys it on partial initialization failure. Shared assets keep their existing lifetimes.
    internal static (Entity Environment, Entity Probes) Acquire(World world, Prepared prepared, Entity owner)
    {
        ArgumentNullException.ThrowIfNull(world); ArgumentNullException.ThrowIfNull(prepared);
        if (!owner.IsValid || !ReferenceEquals(owner.Host.World, world)
            || Unsafe.IsNullRef(ref owner.GetOrNullRef<AssetMetadata>()))
            throw new ArgumentException("Lighting owner must be an asset entity in the target World.", nameof(owner));
        _ = world.GetAddon<AssetLibrary>();
        AssetLibrary.RegisterAsset<IblEnvironmentResource, IblEnvironmentRecord>();
        AssetLibrary.RegisterAsset<DiffuseProbeResource, DiffuseProbeRecord>();
        Entity environment = default;
        if (prepared.EnvironmentContent is { } content) {
            var record = prepared.Environment ?? throw new InvalidAssetException("Declared environment has no prepared record.");
            if (record.Data.Write(Stream.Null) != content.Chunks[0].Id)
                throw new InvalidAssetException("Prepared environment differs from its declared content.");
            var library = world.GetAddon<AssetLibrary>();
            var previous = library.Entities.Values.FirstOrDefault(item => item.Get<AssetMetadata>().Id == content.Id);
            if (previous.IsValid && (Unsafe.IsNullRef(ref previous.GetOrNullRef<IblEnvironmentResource>())
                || previous.Get<IblEnvironmentResource>().Data.Write(Stream.Null) != content.Chunks[0].Id))
                throw new InvalidAssetException("Environment identity already has another kind or content revision.");
            // A capture may use this same immutable payload in another logical slot.
            // Each slot needs a distinct record identity; the payload arrays remain shared.
            environment = world.AcquireAsset(content.Id, () => record with { }, owner);
        }
        else if (prepared.Environment is { } e) environment = world.AcquireAsset(e, owner);
        var probes = prepared.Probes is { } p ? world.AcquireAsset(p, owner) : default;
        return (environment, probes);
    }
}
