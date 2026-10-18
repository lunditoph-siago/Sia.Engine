#if !BROWSER
using Sia.Engine.Rendering;
using Sia.Engine.Rendering.Pbr;
using Sia.Math;

namespace Sia.Engine.Example;

internal static partial class LightmapCook
{
    // JSON authoring input only. Vector arrays avoid serializing math-type swizzle properties.
    internal sealed record Recipe
    {
        public required Guid Product { get; init; }
        public required Guid[] StaticInstances { get; init; }
        public float[] Horizon { get; init; } = [.55f, .6f, .68f];
        public float[] Zenith { get; init; } = [.12f, .24f, .55f];
        public float[] Ground { get; init; } = [.08f, .08f, .07f];
        public float[] SunDirection { get; init; } = [.4f, 1, .3f];
        public float[] SunRadiance { get; init; } = [8, 7.68f, 7.2f];
        public float SunExponent { get; init; } = 256;
        public float SkyIntensity { get; init; } = 1;
        public float[] TowardLight { get; init; } = [0, 1, 0];
        public float[] LightRadiance { get; init; } = [0, 0, 0];
        public int Samples { get; init; } = 128;
        public float MaximumDistance { get; init; } = 1000;
        public float RayBias { get; init; } = .002f;
        public ulong MaximumWorkingBytes { get; init; } = 64ul * 1024 * 1024;
        public ulong MaximumStagingBytes { get; init; } = 512ul * 1024 * 1024;
        public int MaximumPageResolution { get; init; } = 64;

        internal PbrLightmapBakeSettings Settings()
        {
            if (MaximumPageResolution is < 2 or > 8192 || (MaximumPageResolution & (MaximumPageResolution - 1)) != 0)
                throw new InvalidDataException("Lightmap page resolution must be a power of two from 2 to 8192.");
            if (Product == Guid.Empty || StaticInstances is null || StaticInstances.Length is < 1 or > PbrLightmapData.MaximumReceivers
                || StaticInstances.Any(id => id == Guid.Empty) || StaticInstances.Distinct().Count() != StaticInstances.Length)
                throw new InvalidDataException("Lightmap recipe requires a product and unique nonempty static instance IDs.");
            static float3 Vector(float[] values) => values is { Length: 3 } && values.All(float.IsFinite)
                ? new(values[0], values[1], values[2]) : throw new InvalidDataException("Bake vectors require three finite components.");
            return new() {
                Sky = new ProceduralSky { Horizon = Vector(Horizon), Zenith = Vector(Zenith), Ground = Vector(Ground),
                    SunDirection = Vector(SunDirection), SunRadiance = Vector(SunRadiance), SunExponent = SunExponent, Intensity = SkyIntensity },
                TowardLight = Vector(TowardLight), LightRadiance = Vector(LightRadiance), Samples = Samples, MaximumDistance = MaximumDistance, RayBias = RayBias
            };
        }
    }
}
#endif
