using Sia.Engine.Camera;
using Sia.Engine.Lighting;
using Sia.Engine.Mesh;
using Sia.Math;

namespace Sia.Engine.Rendering;

public static class CascadeSplitting
{
    public static float[] ComputeSplitDistances(float near, float far, int cascadeCount, float lambda)
    {
        var splits = new float[cascadeCount + 1];
        splits[0] = near;
        for (var i = 1; i < cascadeCount; i++) {
            var t = (float)i / cascadeCount;
            var logSplit = near * MathF.Pow(far / near, t);
            var uniformSplit = near + ((far - near) * t);
            splits[i] = (lambda * logSplit) + ((1.0f - lambda) * uniformSplit);
        }
        splits[cascadeCount] = far;
        return splits;
    }

    public static float4x4 ComputeCascadeViewProj(
        in AffineTransform cameraWorldTransform,
        float verticalFov,
        float aspect,
        float splitNear,
        float splitFar,
        float3 lightDirection,
        float pullbackMultiplier,
        Aabb? casterBounds = null,
        uint resolution = 0)
    {
        if (resolution > 1 && casterBounds is { } stableBounds)
            return ComputeStableCascade(cameraWorldTransform, verticalFov, aspect, splitNear, splitFar,
                lightDirection, stableBounds, resolution);
        var corners = ComputeFrustumCorners(in cameraWorldTransform, verticalFov, aspect, splitNear, splitFar);

        var center = float3.zero;
        for (var i = 0; i < corners.Length; i++) {
            center += corners[i];
        }
        center /= corners.Length;

        var radius = 0.01f;
        for (var i = 0; i < corners.Length; i++) {
            radius = MathF.Max(radius, math.length(corners[i] - center));
        }

        var up = MathF.Abs(lightDirection.y) > 0.99f ? new float3(0, 0, 1) : new float3(0, 1, 0);
        var lightRotation = quaternion.LookRotation(-lightDirection, up);
        var pullback = radius * pullbackMultiplier;
        if (casterBounds is { } bounds) {
            var lightwardExtent = math.dot(center - bounds.Center, lightDirection)
                + math.dot(bounds.HalfExtents, math.abs(lightDirection));
            pullback = MathF.Max(pullback, lightwardExtent + 0.01f);
        }
        var eye = center - (lightDirection * pullback);
        var lightWorld = float4x4.TRS(eye, lightRotation, float3.one);
        var view = math.inverse(lightWorld);
        var proj = float4x4.Ortho(radius * 2.0f, radius * 2.0f, 0.01f, pullback + radius);
        return math.mul(proj, view);
    }

    private static float4x4 ComputeStableCascade(
        in AffineTransform camera,
        float fov,
        float aspect,
        float near,
        float far,
        float3 direction,
        Aabb bounds,
        uint resolution)
    {
        var halfDepth = (far - near) * .5f;
        var halfHeight = far * MathF.Tan(fov * .5f);
        var radius = MathF.Sqrt((halfDepth * halfDepth) + (halfHeight * halfHeight * (1 + (aspect * aspect))));
        radius *= resolution / (resolution - 1f);
        var center = camera.Translation - (math.normalize(camera.RotationScale.c2) * ((near + far) * .5f));
        var up = MathF.Abs(direction.y) > .99f ? new float3(0, 0, 1) : new float3(0, 1, 0);
        var rotation = quaternion.LookRotation(-direction, up);
        var basis = float4x4.TRS(float3.zero, rotation, float3.one);
        var inverseBasis = math.transpose(basis);
        var centerLight = math.mul(inverseBasis, new float4(center, 1)).xyz;
        var texel = 2 * radius / resolution;
        centerLight.x = MathF.Round(centerLight.x / texel) * texel;
        centerLight.y = MathF.Round(centerLight.y / texel) * texel;
        var zAxis = basis.c2.xyz;
        var zCenter = math.dot(bounds.Center, zAxis);
        var zExtent = math.dot(bounds.HalfExtents, math.abs(zAxis));
        const float padding = .02f;
        centerLight.z = zCenter + zExtent + padding;
        var eye = math.mul(basis, new float4(centerLight, 1)).xyz;
        var view = math.inverse(float4x4.TRS(eye, rotation, float3.one));
        return math.mul(float4x4.Ortho(radius * 2, radius * 2, .01f, (2 * zExtent) + (padding * 2)), view);
    }

    public static bool ContainsReceiverFrustum(
        in float4x4 shadowMatrix,
        in AffineTransform camera,
        float verticalFov,
        float aspect,
        float near,
        float far,
        uint resolution)
    {
        var limit = 1f - (2f / resolution);
        foreach (var corner in ComputeFrustumCorners(camera, verticalFov, aspect, near, far)) {
            var clip = math.mul(shadowMatrix, new float4(corner, 1));
            if (!(clip.w > 0) || !(MathF.Abs(clip.x) <= clip.w * limit) || !(MathF.Abs(clip.y) <= clip.w * limit))
                return false;
        }
        return true;
    }

    public static float4x4 ComputeSpotViewProj(
        in AffineTransform lightWorldTransform,
        float outerAngle,
        float range)
    {
        float4x4 lightWorldMatrix = lightWorldTransform;
        var view = math.inverse(lightWorldMatrix);
        var fov = System.Math.Clamp(outerAngle * 2.0f, 0.05f, MathF.PI - 0.05f);
        var proj = float4x4.PerspectiveFov(fov, 1.0f, 0.05f, range);
        return math.mul(proj, view);
    }

    private static float3[] ComputeFrustumCorners(
        in AffineTransform cameraWorldTransform,
        float verticalFov,
        float aspect,
        float near,
        float far)
    {
        var position = cameraWorldTransform.Translation;
        var forward = -math.normalize(cameraWorldTransform.RotationScale.c2);
        var up = math.normalize(cameraWorldTransform.RotationScale.c1);
        var right = math.normalize(cameraWorldTransform.RotationScale.c0);

        var halfVNear = near * MathF.Tan(verticalFov * 0.5f);
        var halfHNear = halfVNear * aspect;
        var halfVFar = far * MathF.Tan(verticalFov * 0.5f);
        var halfHFar = halfVFar * aspect;

        var centerNear = position + (forward * near);
        var centerFar = position + (forward * far);

        return [
            centerNear + (up * halfVNear) + (right * halfHNear),
            centerNear + (up * halfVNear) - (right * halfHNear),
            centerNear - (up * halfVNear) + (right * halfHNear),
            centerNear - (up * halfVNear) - (right * halfHNear),
            centerFar + (up * halfVFar) + (right * halfHFar),
            centerFar + (up * halfVFar) - (right * halfHFar),
            centerFar - (up * halfVFar) + (right * halfHFar),
            centerFar - (up * halfVFar) - (right * halfHFar),
        ];
    }
}
