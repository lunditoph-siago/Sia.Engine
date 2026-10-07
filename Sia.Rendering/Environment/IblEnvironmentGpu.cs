using Sia;
using Sia.Math;
using Sia.WebGPU;

namespace Sia.Engine.Rendering;

public sealed unsafe class IblEnvironmentGpu : IDisposable
{
    public const int Size = IblEnvironmentAsset.CubeSize;
    public const int Mips = IblEnvironmentAsset.MipCount;
    public const int LutSize = IblEnvironmentAsset.LutSize;
    private const uint k_Samples = 256;

    private readonly GpuResources _gpu;
    private readonly Entity _cube, _lut;
    public Entity CapturedCube { get; }
    public Entity CapturedCubeView { get; }
    private ProceduralSky? _source;
    private readonly IblEnvironmentAsset? _baked;

    public Entity Cube => _cube;

    public Entity Lut => _lut;

    public Entity CubeView { get; }
    public Entity LutView { get; }
    public Entity Sampler { get; }
    public Entity Sh { get; }

    public ulong Bytes => _gpu.Bytes;

    public IblEnvironmentGpu(in GpuFrame frame, IblEnvironmentAsset? baked = null, IblEnvironmentAsset? reflections = null)
    {
        _baked = baked;
        _gpu = new(frame, (reflections is null ? 2ul : 3ul) * 1024 * 1024);
        try {
            var desc = WGPUTextureDescriptor.Default;
            desc.Dimension = WGPUTextureDimension._2D;
            desc.Format = WGPUTextureFormat.RGBA16Float;
            desc.Usage = WGPUTextureUsage.TextureBinding | WGPUTextureUsage.CopyDst;
            desc.Size = new() {
                Width = Size,
                Height = Size,
                DepthOrArrayLayers = 6
            };
            desc.MipLevelCount = Mips;
            var bindingDimension = WGPUTextureBindingViewDimension.Default;
            bindingDimension.TextureBindingViewDimension = WGPUTextureViewDimension.Cube;
            if (OperatingSystem.IsBrowser()) desc.NextInChain = &bindingDimension.Chain;
            _cube = _gpu.Texture(desc, 1048560);
            desc.NextInChain = null;
            var view = WGPUTextureViewDescriptor.Default;
            view.Dimension = WGPUTextureViewDimension.Cube;
            CubeView = _gpu.Own(Wgpu.CreateTextureView(_cube.GetWgpu<WGPUTexture>(), view));
            if (reflections is not null) {
                if (baked is null || reflections.Sky != baked.Sky)
                    throw new ArgumentException("A reflection capture requires a matching baked environment.", nameof(reflections));
                if (OperatingSystem.IsBrowser()) desc.NextInChain = &bindingDimension.Chain;
                CapturedCube = _gpu.Texture(desc, 1048560);
                desc.NextInChain = null;
                CapturedCubeView = _gpu.Own(Wgpu.CreateTextureView(CapturedCube.GetWgpu<WGPUTexture>(), view));
                UploadCube(CapturedCube, reflections);
            }
            desc.Size = new() {
                Width = LutSize,
                Height = LutSize,
                DepthOrArrayLayers = 1
            };
            desc.MipLevelCount = 1;
            _lut = _gpu.Texture(desc, LutSize * LutSize * 8);
            LutView = _gpu.Own(Wgpu.CreateTextureView(_lut.GetWgpu<WGPUTexture>(), WGPUTextureViewDescriptor.Default));
            var sampler = WGPUSamplerDescriptor.Default;
            sampler.AddressModeU = sampler.AddressModeV = sampler.AddressModeW = WGPUAddressMode.ClampToEdge;
            sampler.MinFilter = sampler.MagFilter = WGPUFilterMode.Linear;
            sampler.MipmapFilter = WGPUMipmapFilterMode.Linear;
            // The PBR frame shares this sampler with lightmaps/probes. Each view
            // clamps to its own levels; keep the default sampler's broader LOD range.
            Sampler = _gpu.Own(Wgpu.CreateSampler(_gpu.Device, sampler));
            Sh = _gpu.Buffer(9 * 16, WGPUBufferUsage.Uniform | WGPUBufferUsage.CopyDst);
            if (baked is not null) {
                Upload(baked);
                _source = baked.Sky;
                return;
            }
            var lut = new Half[LutSize * LutSize * 4];
            for (var y = 0; y < LutSize; y++)
                for (var x = 0; x < LutSize; x++) {
                    var nv = (x + .5f) / LutSize;
                    var roughness = (y + .5f) / LutSize;
                    var v = new float3(MathF.Sqrt(1 - (nv * nv)), 0, nv);
                    float scale = 0, bias = 0;
                    for (uint i = 0; i < k_Samples; i++) {
                        var h = Sample(i, roughness, new(0, 0, 1));
                        var vh = MathF.Max(0, math.dot(v, h));
                        var l = (2 * vh * h) - v;
                        var nl = MathF.Max(0, l.z);
                        if (nl == 0) continue;
                        var k = roughness * roughness * .5f;
                        var g = nv / ((nv * (1 - k)) + k) * nl / ((nl * (1 - k)) + k);
                        var visibility = g * vh / MathF.Max(h.z * nv, 1e-4f);
                        var f = MathF.Pow(1 - vh, 5);
                        scale += (1 - f) * visibility;
                        bias += f * visibility;
                    }
                    var offset = ((y * LutSize) + x) * 4;
                    lut[offset] = (Half)(scale / k_Samples);
                    lut[offset + 1] = (Half)(bias / k_Samples);
                    lut[offset + 3] = (Half)1;
                }
            Write(_lut, 0, 0, LutSize, lut);
        }
        catch { _gpu.Dispose(); throw; }
    }

    public void Update(ProceduralSky sky)
    {
        if (sky == _source) return;
        sky.Validate();
        if (_baked is not null)
            throw new InvalidOperationException("Baked environment sky differs from scene lighting; select matching bake data or the procedural provider.");
        var coefficients = IrradianceSh.Project(sky.Evaluate);
        Wgpu.WriteBuffer<float4>(_gpu.Queue, Sh.GetWgpu<WGPUBuffer>(), 0, coefficients);
        for (var mip = 0; mip < Mips; mip++) {
            var size = Size >> mip;
            var roughness = (float)mip / (Mips - 1);
            var data = new Half[size * size * 4];
            for (var face = 0; face < 6; face++) {
                for (var y = 0; y < size; y++)
                    for (var x = 0; x < size; x++) {
                        var u = ((x + .5f) / size * 2) - 1;
                        var v = ((y + .5f) / size * 2) - 1;
                        var n = math.normalize(face switch {
                            0 => new float3(1, -v, -u),
                            1 => new(-1, -v, u),
                            2 => new(u, 1, v),
                            3 => new(u, -1, -v),
                            4 => new(u, -v, 1),
                            _ => new(-u, -v, -1)
                        });
                        var color = float3.zero;
                        float weight = 0;
                        var count = mip == 0 ? 1u : k_Samples;
                        for (uint i = 0; i < count; i++) {
                            var h = Sample(i, roughness, n);
                            var l = (2 * math.dot(n, h) * h) - n;
                            var nl = MathF.Max(0, math.dot(n, l));
                            color += sky.Evaluate(l) * nl;
                            weight += nl;
                        }
                        color /= MathF.Max(weight, 1e-6f);
                        var offset = ((y * size) + x) * 4;
                        data[offset] = (Half)MathF.Min(color.x, 65504);
                        data[offset + 1] = (Half)MathF.Min(color.y, 65504);
                        data[offset + 2] = (Half)MathF.Min(color.z, 65504);
                        data[offset + 3] = (Half)1;
                    }
                Write(_cube, mip, face, size, data);
            }
        }
        _source = sky;
    }

    private void Upload(IblEnvironmentAsset asset)
    {
        Wgpu.WriteBuffer<float4>(_gpu.Queue, Sh.GetWgpu<WGPUBuffer>(), 0, asset.Coefficients.Span);
        Write(_lut, 0, 0, LutSize, asset.BrdfLut.Span);
        UploadCube(_cube, asset);
    }

    private void UploadCube(Entity cube, IblEnvironmentAsset asset)
    {
        var offset = 0;
        for (var mip = 0; mip < Mips; mip++) {
            var size = Size >> mip;
            var length = size * size * 4;
            for (var face = 0; face < 6; face++) {
                Write(cube, mip, face, size, asset.Cube.Span.Slice(offset, length));
                offset += length;
            }
        }
    }

    internal static float3 Sample(uint index, float roughness, float3 n)
    {
        var bits = index;
        bits = (bits << 16) | (bits >> 16);
        bits = ((bits & 0x55555555) << 1) | ((bits & 0xaaaaaaaa) >> 1);
        bits = ((bits & 0x33333333) << 2) | ((bits & 0xcccccccc) >> 2);
        bits = ((bits & 0x0f0f0f0f) << 4) | ((bits & 0xf0f0f0f0) >> 4);
        bits = ((bits & 0x00ff00ff) << 8) | ((bits & 0xff00ff00) >> 8);
        var y = bits * 2.3283064365386963e-10f;
        var a = roughness * roughness;
        var phi = MathF.Tau * index / k_Samples;
        var cosine = MathF.Sqrt((1 - y) / (1 + (((a * a) - 1) * y)));
        var sine = MathF.Sqrt(MathF.Max(0, 1 - (cosine * cosine)));
        var t = math.normalize(math.cross(MathF.Abs(n.z) < .999f ? new float3(0, 0, 1) : new(1, 0, 0), n));
        return math.normalize((t * (MathF.Cos(phi) * sine)) + (math.cross(n, t) * (MathF.Sin(phi) * sine)) + (n * cosine));
    }

    private void Write(Entity texture, int mip, int layer, int size, ReadOnlySpan<Half> values)
    {
        var target = new WGPUTexelCopyTextureInfo {
            Texture = (WGPUTexture*)texture.GetWgpu<WGPUTexture>().DangerousGetHandle(),
            MipLevel = (uint)mip,
            Origin = new() { Z = (uint)layer },
            Aspect = WGPUTextureAspect.All
        };
        var layout = new WGPUTexelCopyBufferLayout { BytesPerRow = (uint)size * 8, RowsPerImage = (uint)size };
        var extent = new WGPUExtent3D { Width = (uint)size, Height = (uint)size, DepthOrArrayLayers = 1 };
        fixed (Half* data = values)
            WgpuUnsafe.wgpuQueueWriteTexture((WGPUQueue*)_gpu.Queue.DangerousGetHandle(), &target,
            data, (nuint)values.Length * 2, &layout, &extent);
    }

    public void Dispose() => _gpu.Dispose();
}
