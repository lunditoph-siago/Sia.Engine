using Sia;
using Sia.Math;
using Sia.WebGPU;

namespace Sia.Engine.Rendering;

public sealed unsafe class IblEnvironmentGpu : IDisposable
{
    public const int Size = IblEnvironmentAsset.CubeSize;
    public const int Mips = IblEnvironmentAsset.MipCount;
    public const int LutSize = IblEnvironmentAsset.LutSize;

    private readonly GpuResources _gpu;
    private readonly Entity _cube, _lut;
    private ProceduralSky? _source;
    private readonly IblEnvironmentAsset? _baked;
    private float _proceduralScale = 1;

    public Entity Cube => _cube;

    public Entity Lut => _lut;

    public Entity CubeView { get; }
    public Entity LutView { get; }
    public Entity Sampler { get; }
    public Entity Sh { get; }
    public float CubeRadianceScale => _baked?.CubeRadianceScale ?? _proceduralScale;
    /// <summary>Conservative absolute physical cube radiance, including all filtered levels.</summary>
    public double MaximumRadiance { get; private set; }
    /// <summary>Conservative absolute physical diffuse irradiance for unit normals.</summary>
    public double MaximumIrradiance { get; private set; }
    /// <summary>Absolute dimensionless maxima of the two sampled BRDF coefficients.</summary>
    public float2 BrdfBounds { get; private set; }

    public ulong Bytes => _gpu.Bytes;

    public IblEnvironmentGpu(in GpuFrame frame, IblEnvironmentAsset? baked = null)
    {
        _baked = baked;
        _gpu = new(frame, 2 * 1024 * 1024);
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
            sampler.LodMaxClamp = Mips - 1;
            Sampler = _gpu.Own(Wgpu.CreateSampler(_gpu.Device, sampler));
            Sh = _gpu.Buffer(9 * 16, WGPUBufferUsage.Uniform | WGPUBufferUsage.CopyDst);
            if (baked is not null) {
                Upload(baked);
                _source = baked.Sky;
                return;
            }
            var lut = IblEnvironmentData.BrdfLut;
            for (var at = 0; at < lut.Length; at += 4)
                BrdfBounds = math.max(BrdfBounds, new float2(MathF.Abs((float)lut[at]), MathF.Abs((float)lut[at + 1])));
            Write(_lut, 0, 0, LutSize, lut);
        }
        catch { _gpu.Dispose(); throw; }
    }

    public void Update(ProceduralSky sky)
    {
        if (sky == _source) return;
        ArgumentNullException.ThrowIfNull(sky); sky.ValidateData();
        if (_baked is not null)
            throw new InvalidOperationException("Baked environment sky differs from scene lighting; select matching bake data or the procedural provider.");
        var data = IblEnvironmentData.Get(sky);
        UploadEnvironment(data);
        _source = sky;
        _proceduralScale = data.CubeRadianceScale;
        MaximumRadiance = IblEnvironmentData.MaximumRadiance(sky);
        MaximumIrradiance = IrradianceSh.Bound(data.Coefficients.Span);
    }

    private void Upload(IblEnvironmentAsset asset)
    {
        UploadEnvironment(asset);
        var lut = asset.BrdfLut.Span;
        for (var at = 0; at < lut.Length; at += 4)
            BrdfBounds = math.max(BrdfBounds, new float2(MathF.Abs((float)lut[at]), MathF.Abs((float)lut[at + 1])));
        Write(_lut, 0, 0, LutSize, asset.BrdfLut.Span);
    }

    private void UploadEnvironment(IblEnvironmentAsset asset)
    {
        var peak = 0f;
        var cube = asset.Cube.Span;
        for (var at = 0; at < cube.Length; at += 4)
            peak = MathF.Max(peak, MathF.Max(MathF.Abs((float)cube[at]), MathF.Max(MathF.Abs((float)cube[at + 1]), MathF.Abs((float)cube[at + 2]))));
        MaximumRadiance = (double)peak * asset.CubeRadianceScale;
        MaximumIrradiance = IrradianceSh.Bound(asset.Coefficients.Span);
        Wgpu.WriteBuffer<float4>(_gpu.Queue, Sh.GetWgpu<WGPUBuffer>(), 0, asset.Coefficients.Span);
        var offset = 0;
        for (var mip = 0; mip < Mips; mip++) {
            var size = Size >> mip;
            var length = size * size * 4;
            for (var face = 0; face < 6; face++) {
                Write(_cube, mip, face, size, asset.Cube.Span.Slice(offset, length));
                offset += length;
            }
        }
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
