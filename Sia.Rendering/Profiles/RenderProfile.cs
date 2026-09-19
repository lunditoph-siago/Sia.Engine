namespace Sia.Engine.Rendering;

public enum RenderQuality { Low, Medium, High }

/// <summary>Enabled device limits, independent of API names, platform and material model.</summary>
public readonly record struct RenderCapabilities(ulong MaxBufferBytes, ulong MaxStorageBufferBytes,
    uint MaxTextureDimension2D, uint MaxTextureArrayLayers, bool TimestampQueries);

/// <summary>Explicit quality and residency policy; device limits do not predict device speed.</summary>
public sealed record RenderProfile(RenderQuality Quality, long DetailGeometryBytes, int UploadBytesPerFrame,
    uint ShadowResolution, bool ScreenSpaceReflections, bool ScreenSpaceIndirectLighting)
{
    public static RenderProfile For(RenderQuality quality) => quality switch {
        RenderQuality.Low => new(quality, 24 * 1024 * 1024, 256 * 1024, 256, false, false),
        RenderQuality.Medium => new(quality, 64 * 1024 * 1024, 1024 * 1024, 512, true, true),
        RenderQuality.High => new(quality, 128 * 1024 * 1024, 2 * 1024 * 1024, 1024, true, true),
        _ => throw new ArgumentOutOfRangeException(nameof(quality))
    };

    public RenderProfile Resolve(RenderCapabilities capabilities)
    {
        Validate();
        if (capabilities.MaxBufferBytes < 1024 * 1024 || capabilities.MaxStorageBufferBytes < 1024 * 1024
            || capabilities.MaxTextureDimension2D < 1 || capabilities.MaxTextureArrayLayers < 1)
            throw new NotSupportedException("The device cannot provide the minimum rendering resources.");
        var geometry = System.Math.Min((ulong)DetailGeometryBytes, System.Math.Min(capabilities.MaxBufferBytes, capabilities.MaxStorageBufferBytes));
        return this with {
            DetailGeometryBytes = (long)(geometry / (1024 * 1024) * (1024 * 1024)),
            ShadowResolution = System.Math.Min(ShadowResolution, capabilities.MaxTextureDimension2D)
        };
    }

    public void Validate()
    {
        if (!Enum.IsDefined(Quality) || DetailGeometryBytes is < 1024 * 1024 or > 256 * 1024 * 1024
            || UploadBytesPerFrame < 65536 || UploadBytesPerFrame % 16 != 0 || ShadowResolution == 0)
            throw new ArgumentOutOfRangeException(nameof(RenderProfile), "Invalid rendering budgets.");
    }
}
