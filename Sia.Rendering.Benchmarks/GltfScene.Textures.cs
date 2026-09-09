using System.Text.Json;
using Sia.Engine.Rendering.Pbr;
using Sia.Math;
using Sia.WebGPU;
using StbImageSharp;

namespace Sia.Engine.Rendering.Benchmarks;

internal sealed partial class GltfScene
{
    private readonly Dictionary<(int Index, bool Srgb, bool Normal), PbrTextureData> _textures = [];

    private PbrMaterialAsset Material(JsonElement material)
    {
        Reject(material, "extensions");
        var blend = material.TryGetProperty("alphaMode", out var alphaMode) && alphaMode.GetString() == "BLEND";
        Require(!material.TryGetProperty("alphaMode", out alphaMode) || alphaMode.GetString() is "OPAQUE" or "BLEND", "Unsupported glTF alpha mode.");
        var doubleSided = material.TryGetProperty("doubleSided", out var sided) && sided.GetBoolean();
        var color = new[] { 1f, 1f, 1f, 1f }; var metallic = 1f; var roughness = 1f;
        PbrTextureData? baseColor = null; PbrTextureData? metallicRoughness = null;
        if (material.TryGetProperty("pbrMetallicRoughness", out var pbr)) {
            Reject(pbr, "extensions");
            if (pbr.TryGetProperty("baseColorFactor", out var factor)) { color = Floats(factor, 4); }
            metallic = Scalar(pbr, "metallicFactor", 1); roughness = Scalar(pbr, "roughnessFactor", 1);
            baseColor = Texture(pbr, "baseColorTexture", srgb: true);
            metallicRoughness = Texture(pbr, "metallicRoughnessTexture");
        }
        var emissive = material.TryGetProperty("emissiveFactor", out var emission) ? Floats(emission, 3) : [0f, 0f, 0f];
        Require(color.All(value => value is >= 0 and <= 1) && emissive.All(value => value is >= 0 and <= 1), "Invalid glTF material color factor.");
        return new(new(new(color[0], color[1], color[2]), metallic, roughness, new(emissive[0], emissive[1], emissive[2]), 1),
            baseColor, Texture(material, "normalTexture", normal: true), metallicRoughness, Texture(material, "occlusionTexture"),
            Texture(material, "emissiveTexture", srgb: true),
            material.TryGetProperty("normalTexture", out var normalInfo) ? Scalar(normalInfo, "scale", 1) : 1,
            material.TryGetProperty("occlusionTexture", out var occlusionInfo) ? Scalar(occlusionInfo, "strength", 1) : 1,
            doubleSided, blend, color[3]);
    }

    private PbrTextureData? Texture(JsonElement owner, string property, bool srgb = false, bool normal = false)
    {
        if (!owner.TryGetProperty(property, out var info)) { return null; }
        Reject(info, "extensions");
        Require(Integer(info, "texCoord", 0) == 0, "Only glTF TEXCOORD_0 textures are supported.");
        var index = info.GetProperty("index").GetInt32();
        var key = (index, srgb, normal);
        if (_textures.TryGetValue(key, out var existing)) { return existing; }
        var texture = Item(_root, "textures", index);
        Reject(texture, "extensions");
        var image = Item(_root, "images", texture.GetProperty("source").GetInt32());
        Reject(image, "extensions");
        var bytes = image.TryGetProperty("uri", out var uri) ? Resource(uri.GetString()!)
            : BufferView(Item(_root, "bufferViews", image.GetProperty("bufferView").GetInt32())).ToArray();
        using var imageStream = new MemoryStream(bytes, writable: false);
        var header = ImageInfo.FromStream(imageStream);
        Require(header is { Width: > 0 and <= 8192, Height: > 0 and <= 8192 }, "Invalid glTF image dimensions; maximum is 8192 per axis.");
        _cancellation.ThrowIfCancellationRequested();
        var decoded = ImageResult.FromMemory(bytes, ColorComponents.RedGreenBlueAlpha);
        var sampler = texture.TryGetProperty("sampler", out var samplerIndex) ? Sampler(Item(_root, "samplers", samplerIndex.GetInt32())) : PbrTextureSampler.Default;
        var width = decoded.Width; var height = decoded.Height; var pixels = decoded.Data;
        var scale = System.Math.Min(1f, (float)_textureSize / System.Math.Max(width, height));
        var targetWidth = System.Math.Max(1, (int)MathF.Round(width * scale));
        var targetHeight = System.Math.Max(1, (int)MathF.Round(height * scale));
        if (width != targetWidth || height != targetHeight) { pixels = Resize(pixels, width, height, targetWidth, targetHeight, srgb, normal); }
        width = targetWidth; height = targetHeight;
        var levels = new List<ReadOnlyMemory<byte>> { pixels };
        while (sampler.UseMipmaps && (width > 1 || height > 1)) {
            var nextWidth = System.Math.Max(1, width / 2); var nextHeight = System.Math.Max(1, height / 2);
            pixels = Resize(pixels, width, height, nextWidth, nextHeight, srgb, normal);
            levels.Add(pixels); width = nextWidth; height = nextHeight;
        }
        var result = PbrTextureData.Create((uint)targetWidth, (uint)targetHeight, srgb, levels.ToArray(), sampler);
        _textures.Add(key, result);
        return result;
    }

    private static PbrTextureSampler Sampler(JsonElement sampler)
    {
        Reject(sampler, "extensions");
        var min = Integer(sampler, "minFilter", 9987); var mag = Integer(sampler, "magFilter", 9729);
        Require(min is 9728 or 9729 or 9984 or 9985 or 9986 or 9987 && mag is 9728 or 9729, "Invalid glTF sampler filter.");
        return new(Wrap(Integer(sampler, "wrapS", 10497)), Wrap(Integer(sampler, "wrapT", 10497)),
            min is 9728 or 9984 or 9986 ? WGPUFilterMode.Nearest : WGPUFilterMode.Linear,
            mag == 9728 ? WGPUFilterMode.Nearest : WGPUFilterMode.Linear,
            min is 9986 or 9987 ? WGPUMipmapFilterMode.Linear : WGPUMipmapFilterMode.Nearest, min is not (9728 or 9729));
    }

    private static WGPUAddressMode Wrap(int value) => value switch {
        10497 => WGPUAddressMode.Repeat, 33648 => WGPUAddressMode.MirrorRepeat, 33071 => WGPUAddressMode.ClampToEdge,
        _ => throw new InvalidDataException("Invalid glTF sampler address mode.")
    };

    internal static byte[] Resize(byte[] source, int width, int height, int targetWidth, int targetHeight, bool srgb, bool normal)
    {
        var result = new byte[checked(targetWidth * targetHeight * 4)];
        for (var y = 0; y < targetHeight; y++) {
            var top = (double)y * height / targetHeight; var bottom = (double)(y + 1) * height / targetHeight;
            for (var x = 0; x < targetWidth; x++) {
                var left = (double)x * width / targetWidth; var right = (double)(x + 1) * width / targetWidth;
                var sum = float4.zero; var weight = 0f;
                for (var sy = (int)top; sy < (int)System.Math.Ceiling(bottom); sy++) {
                    for (var sx = (int)left; sx < (int)System.Math.Ceiling(right); sx++) {
                        var area = (float)((System.Math.Min(right, sx + 1) - System.Math.Max(left, sx))
                            * (System.Math.Min(bottom, sy + 1) - System.Math.Max(top, sy)));
                        var at = (sy * width + sx) * 4;
                        var value = new float4(source[at], source[at + 1], source[at + 2], source[at + 3]) / 255f;
                        if (srgb) { value = new(new float3(Linear(value.x), Linear(value.y), Linear(value.z)), value.w); }
                        if (normal) { value = new(value.xyz * 2 - 1, value.w); }
                        sum += value * area; weight += area;
                    }
                }
                sum /= weight;
                if (normal) { sum = new((math.lengthsq(sum.xyz) > 1e-12f ? math.normalize(sum.xyz) : new float3(0, 0, 1)) * 0.5f + 0.5f, sum.w); }
                if (srgb) { sum = new(new float3(Srgb(sum.x), Srgb(sum.y), Srgb(sum.z)), sum.w); }
                var target = (y * targetWidth + x) * 4;
                result[target] = Byte(sum.x); result[target + 1] = Byte(sum.y); result[target + 2] = Byte(sum.z); result[target + 3] = Byte(sum.w);
            }
        }
        return result;
    }

    private static float Linear(float value) => value <= 0.04045f ? value / 12.92f : MathF.Pow((value + 0.055f) / 1.055f, 2.4f);
    private static float Srgb(float value) => value <= 0.0031308f ? value * 12.92f : 1.055f * MathF.Pow(value, 1 / 2.4f) - 0.055f;
    private static byte Byte(float value) => (byte)System.Math.Clamp((int)MathF.Round(value * 255), 0, 255);
}
