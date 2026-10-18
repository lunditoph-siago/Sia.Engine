using Sia.Math;

namespace Sia.Engine.Rendering.Pbr;

public readonly record struct PbrReflectionInstancePose(Guid Id, float4x4 Transform, bool Enabled);

/// <summary>One current or previous linear-radiance sample, with authored identities and geometric validation values.</summary>
public readonly record struct PbrReflectionHistorySample(float3 Radiance, bool Valid, bool Complete,
    Guid Receiver, float3 Position, float3 Normal, float Roughness, Guid Hit, int Primitive, float3 HitPosition, float HitDistance);

public sealed record PbrReflectionHistorySettings
{
    public float Weight { get; init; } = .85f;
    public float NormalCosine { get; init; } = .98f;
    public float RoughnessTolerance { get; init; } = .05f;
    public float PositionTolerance { get; init; } = .01f;
    public float HitTolerance { get; init; } = .02f;
    public float CameraCutDistance { get; init; } = 10;

    public void Validate()
    {
        if (!float.IsFinite(Weight) || Weight < 0 || Weight > .95f || !float.IsFinite(NormalCosine) || NormalCosine < 0 || NormalCosine > 1
            || !float.IsFinite(RoughnessTolerance) || RoughnessTolerance < 0 || RoughnessTolerance > 1
            || !float.IsFinite(PositionTolerance) || PositionTolerance <= 0 || !float.IsFinite(HitTolerance) || HitTolerance <= 0
            || !float.IsFinite(CameraCutDistance) || CameraCutDistance <= 0)
            throw new ArgumentException("Invalid reflection history validation/weight inputs.");
    }
}

/// <summary>Pure rigid reprojection and linear-radiance validation. View ownership and publication belong to the caller.</summary>
public static class PbrReflectionHistory
{
    public static bool IsRigid(float4x4 transform)
    {
        if (!PbrSceneData.ValidTransform(transform)) return false;
        var x = transform.c0.xyz; var y = transform.c1.xyz; var z = transform.c2.xyz;
        return MathF.Abs(math.lengthsq(x) - 1) <= 1e-4f && MathF.Abs(math.lengthsq(y) - 1) <= 1e-4f
            && MathF.Abs(math.lengthsq(z) - 1) <= 1e-4f && MathF.Abs(math.dot(x, y)) <= 1e-4f
            && MathF.Abs(math.dot(x, z)) <= 1e-4f && MathF.Abs(math.dot(y, z)) <= 1e-4f;
    }

    public static bool TryReproject(float3 position, float3 normal, PbrReflectionInstancePose current,
        PbrReflectionInstancePose previous, out float3 previousPosition, out float3 previousNormal)
    {
        previousPosition = previousNormal = default;
        if (current.Id == Guid.Empty || current.Id != previous.Id || !current.Enabled || !previous.Enabled
            || !IsRigid(current.Transform) || !IsRigid(previous.Transform) || !Finite(position) || !Unit(normal)) return false;
        var local = math.mul(math.transpose(new float3x3(current.Transform.c0.xyz, current.Transform.c1.xyz, current.Transform.c2.xyz)), position - current.Transform.c3.xyz);
        var localNormal = math.mul(math.transpose(new float3x3(current.Transform.c0.xyz, current.Transform.c1.xyz, current.Transform.c2.xyz)), normal);
        previousPosition = math.mul(previous.Transform, new float4(local, 1)).xyz;
        previousNormal = math.mul(previous.Transform, new float4(localNormal, 0)).xyz;
        return Finite(previousPosition) && Unit(previousNormal);
    }

    public static bool TryProject(float3 position, float4x4 previousViewProjection, uint width, uint height, out int2 pixel)
    {
        pixel = default;
        if (!Finite(position) || width == 0 || height == 0 || width > 16384 || height > 16384) return false;
        var clip = math.mul(previousViewProjection, new float4(position, 1));
        if (!math.all(math.isfinite(clip)) || clip.w <= 0) return false;
        var ndc = clip.xyz / clip.w;
        if (ndc.z < 0 || ndc.z > 1) return false;
        var uv = ndc.xy * new float2(.5f, -.5f) + new float2(.5f);
        pixel = new((int)MathF.Floor(uv.x * width), (int)MathF.Floor(uv.y * height));
        return pixel.x >= 0 && pixel.y >= 0 && pixel.x < width && pixel.y < height;
    }

    public static bool TryResolve(PbrReflectionHistorySample current, PbrReflectionHistorySample previous,
        PbrReflectionInstancePose receiverNow, PbrReflectionInstancePose receiverThen,
        PbrReflectionInstancePose hitNow, PbrReflectionInstancePose hitThen,
        ReadOnlySpan<PbrReflectionHistorySample> neighborhood, PbrReflectionHistorySettings settings,
        out float3 radiance)
    {
        ArgumentNullException.ThrowIfNull(settings); settings.Validate(); radiance = current.Radiance;
        if (neighborhood.Length > 9) throw new ArgumentException("Reflection clipping uses at most a 3x3 neighborhood.", nameof(neighborhood));
        if (!Valid(current) || !Valid(previous) || current.Receiver != previous.Receiver || current.Hit != previous.Hit || current.Primitive != previous.Primitive
            || current.Receiver != receiverNow.Id || current.Hit != hitNow.Id || MathF.Abs(current.Roughness - previous.Roughness) > settings.RoughnessTolerance)
            return false;
        if (!TryReproject(current.Position, current.Normal, receiverNow, receiverThen, out var receiverPosition, out var receiverNormal)
            || math.distance(receiverPosition, previous.Position) > settings.PositionTolerance
            || math.dot(receiverNormal, previous.Normal) < settings.NormalCosine
            || !TryReproject(current.HitPosition, current.Normal, hitNow, hitThen, out var hitPosition, out _)
            || math.distance(hitPosition, previous.HitPosition) > settings.HitTolerance
            || MathF.Abs(math.distance(hitPosition, receiverPosition) - previous.HitDistance) > settings.HitTolerance)
            return false;
        var minimum = current.Radiance; var maximum = current.Radiance;
        foreach (var value in neighborhood) {
            if (!Valid(value) || value.Receiver != current.Receiver || value.Hit != current.Hit || value.Primitive != current.Primitive
                || math.dot(value.Normal, current.Normal) < settings.NormalCosine || MathF.Abs(value.Roughness - current.Roughness) > settings.RoughnessTolerance
                || math.distance(value.Position, current.Position) > settings.PositionTolerance * 4) continue;
            minimum = math.min(minimum, value.Radiance); maximum = math.max(maximum, value.Radiance);
        }
        var clipped = math.clamp(previous.Radiance, minimum, maximum);
        radiance = math.lerp(current.Radiance, clipped, settings.Weight); return true;
    }

    private static bool Finite(float3 value) => math.all(math.isfinite(value));
    private static bool Unit(float3 value) => Finite(value) && MathF.Abs(math.lengthsq(value) - 1) < .01f;
    private static bool Valid(PbrReflectionHistorySample value)
        => value.Valid && value.Complete && value.Receiver != Guid.Empty && value.Hit != Guid.Empty && value.Primitive >= 0
            && Finite(value.Radiance) && math.all(value.Radiance >= 0) && Finite(value.Position) && Unit(value.Normal)
            && float.IsFinite(value.Roughness) && value.Roughness is >= .045f and <= 1 && Finite(value.HitPosition)
            && float.IsFinite(value.HitDistance) && value.HitDistance > 0;
}
