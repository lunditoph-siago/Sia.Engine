using System.Runtime.InteropServices;
using Sia;
using Sia.Math;
using Sia.WebGPU;

namespace Sia.Engine.Rendering.Pbr;

[StructLayout(LayoutKind.Sequential)]
internal readonly record struct PbrMaterialGpu(
    float4 ColorMetallic,
    float4 EmissiveRoughness,
    float4 Factors,
    uint4 Layers,
    uint4 Indices,
    float4 Transport);

internal sealed partial class PbrMaterials : IDisposable
{
    private sealed class ArrayTexture(Entity texture, Entity view, Entity sampler)
    {
        public readonly Entity Sampler = sampler;
        public Entity Texture = texture, View = view;
        public PbrTextureData[] Sources = [];
        public uint Width, Height;
        public int Levels, TailMip, ResidentMip, WantedMip, DesiredMip;
        public long RetryFrame;
        public int CoarserFrames;
        public float Priority;
        public TextureArrayUpload? Residency;
    }

    private readonly GpuResources _gpu;

    public Entity Table { get; }
    public Entity[] Groups { get; }
    public int[] MaterialBatches { get; }
    public bool[] Transmission { get; }
    public bool HasTransmission { get; }
    public List<Entity> Textures { get; } = [];
    public List<Entity> Uniforms { get; } = [];

    public ulong Bytes {
        get {
            var bytes = _gpu.Bytes;
            foreach (var array in _arrays) bytes = checked(bytes + (array.Residency?.Bytes ?? 0));
            foreach (var (Upload, Completion) in _retired) bytes = checked(bytes + Upload.Bytes);
            return checked(bytes + (_pending?.Upload?.Bytes ?? 0));
        }
    }

    public PbrMaterials(in GpuFrame frame, ReadOnlySpan<PbrMaterialAsset> source, Entity layout, ulong budget,
        PbrSceneStream? stream = null, PbrTextureStreamingSettings? streaming = null)
    {
        (_frame, _layout, _budget, _stream, _streaming) = (frame, layout, budget, stream, streaming ?? new());
        if (_streaming.UploadBytesPerFrame < 4 || _streaming.UploadBytesPerFrame % 4 != 0
            || _streaming.DecodedBytes < 65536 || _streaming.DecodedBytes > 256L * 1024 * 1024 || _streaming.IdleFrames < 1
            || !float.IsFinite(_streaming.MipBias) || _streaming.MipBias is < 0 or > 4)
            throw new ArgumentOutOfRangeException(nameof(streaming));
        _gpu = new(frame, budget);
        try {
            var assets = source.IsEmpty ? [new PbrMaterialAsset(PbrMaterial.Default)] : source.ToArray();
            if (assets.Length > 256)
                throw new ArgumentException("The resident material table supports at most 256 materials.");
            HasTransmission = assets.Any(m => m.Transmission > 0);
            Transmission = [.. assets.Select(m => m.Transmission > 0)];
            var whiteSrgb = PbrTextureData.Create(1, 1, true, [new byte[] { 255, 255, 255, 255 }]);
            var white = PbrTextureData.Create(1, 1, false, [new byte[] { 255, 255, 255, 255 }]);
            var normal = PbrTextureData.Create(1, 1, false, [new byte[] { 128, 128, 255, 255 }]);
            var maps = assets.Select(m => new[] { m.BaseColor ?? whiteSrgb, m.Normal ?? normal,
                m.MetallicRoughness ?? white, m.Occlusion ?? white, m.Emissive ?? whiteSrgb }).ToArray();
            var references = new Dictionary<PbrTextureData, (ArrayTexture Texture, uint Layer)>(ReferenceEqualityComparer.Instance);
            foreach (var shape in maps.SelectMany(m => m).Distinct<PbrTextureData>(ReferenceEqualityComparer.Instance)
                .GroupBy(Shape)) {
                var (Width, Height, Levels, Srgb, Sampler) = shape.Key;
                var perLayer = TailBytes(Width, Height, Levels, 0);
                var layerLimit = stream is null ? (int)_gpu.Limits.MaxTextureArrayLayers
                    : (int)System.Math.Min(_gpu.Limits.MaxTextureArrayLayers, System.Math.Max(1L, _streaming.DecodedBytes / perLayer));
                foreach (var textures in shape.Chunk(layerLimit)) {
                    var array = stream is not null && stream.TextureSources.ContainsKey(textures[0])
                        ? UploadStreamTail(textures) : UploadArray(textures);
                    _arrays.Add(array);
                    for (var i = 0; i < textures.Length; i++)
                        references.Add(textures[i], (array, (uint)i));
                }
            }
            _materialArrays = [.. maps.Select(m => m.Select(t => references[t].Texture).Distinct().ToArray())];
            var batchMaps = new List<ArrayTexture[]>();
            MaterialBatches = new int[assets.Length];
            var parameters = new PbrMaterialGpu[assets.Length];
            for (var i = 0; i < assets.Length; i++) {
                var m = assets[i];
                var p = m.Parameters;
                var entries = maps[i].Select(t => references[t]).ToArray();
                var arrays = entries.Select(e => e.Texture).ToArray();
                var batch = batchMaps.FindIndex(b => b.AsSpan().SequenceEqual(arrays));
                if (batch < 0) {
                    batch = batchMaps.Count;
                    batchMaps.Add(arrays);
                }
                MaterialBatches[i] = batch;
                var textureFlags = (m.BaseColor is null ? 0u : 1u)
                    | (m.MetallicRoughness is null ? 0u : 2u)
                    | (m.Occlusion is null || m.OcclusionStrength == 0 ? 0u : 4u)
                    | (m.Emissive is null || p.EmissiveStrength == 0 ? 0u : 8u);
                parameters[i] = new(new(p.BaseColor, p.Metallic), new(p.EmissiveColor * p.EmissiveStrength, p.Roughness),
                    new(m.NormalScale, m.OcclusionStrength, m.Normal is null ? 0 : 1, m.DoubleSided ? 1 : 0),
                    new(entries[0].Layer, entries[1].Layer, entries[2].Layer, entries[3].Layer),
                    new(entries[4].Layer, (uint)batch, textureFlags, 0), new(m.Opacity, m.Transmission, m.Thickness, 0));
            }
            var tableBytes = checked(((ulong)parameters.Length * (ulong)Marshal.SizeOf<PbrMaterialGpu>())
                + ((ulong)batchMaps.Count * 16));
            if (tableBytes > budget - Bytes)
                throw new InvalidOperationException("Combined material allocation budget exceeded.");
            Table = _gpu.Upload<PbrMaterialGpu>(parameters);
            _batchMaps = batchMaps;
            Groups = new Entity[batchMaps.Count];
            for (var batch = 0; batch < Groups.Length; batch++) {
                var uniform = _gpu.Upload<uint4>([new((uint)batch, 0, 0, 0)], WGPUBufferUsage.Uniform);
                Uniforms.Add(uniform);
                var entries = new WGPUBindGroupEntry[12];
                entries[0] = GpuBinding.Buffer(0, Table);
                entries[1] = GpuBinding.Buffer(1, uniform);
                for (uint map = 0; map < 5; map++) {
                    entries[2 + (map * 2)] = GpuBinding.Texture(2 + (map * 2), batchMaps[batch][map].View.GetWgpu<WGPUTextureView>());
                    entries[3 + (map * 2)] = GpuBinding.Sampler(3 + (map * 2), batchMaps[batch][map].Sampler);
                }
                Groups[batch] = GpuBinding.Group(_gpu, layout, entries);
            }
            if (Bytes > budget) throw new InvalidOperationException("Combined material allocation budget exceeded.");
            _peak = Bytes;
        }
        catch {
            foreach (var array in _arrays) array.Residency?.Dispose();
            _gpu.Dispose();
            throw;
        }
    }

    private unsafe ArrayTexture UploadArray(PbrTextureData[] sources)
    {
        var first = sources[0];
        var dimension = WGPUTextureBindingViewDimension.Default;
        dimension.TextureBindingViewDimension = WGPUTextureViewDimension._2DArray;
        var descriptor = WGPUTextureDescriptor.Default;
        descriptor.NextInChain = &dimension.Chain;
        descriptor.Size = new() {
            Width = first.Width, Height = first.Height, DepthOrArrayLayers = (uint)sources.Length
        };
        descriptor.Dimension = WGPUTextureDimension._2D;
        descriptor.Format = first.Srgb ? WGPUTextureFormat.RGBA8UnormSrgb : WGPUTextureFormat.RGBA8Unorm;
        descriptor.MipLevelCount = (uint)first.MipLevels.Length;
        descriptor.Usage = WGPUTextureUsage.TextureBinding | WGPUTextureUsage.CopyDst;
        ulong bytes = 0;
        foreach (var source in sources)
            foreach (var level in source.MipLevels.Span)
                bytes = checked(bytes + (ulong)level.Length);
        if (bytes > _budget - Bytes)
            throw new InvalidOperationException("Combined material allocation budget exceeded.");
        var texture = _gpu.Texture(descriptor, bytes);
        Textures.Add(texture);
        for (var layer = 0; layer < sources.Length; layer++) {
            for (var mip = 0; mip < first.MipLevels.Length; mip++) {
                var width = System.Math.Max(1u, first.Width >> mip);
                var height = System.Math.Max(1u, first.Height >> mip);
                var destination = new WGPUTexelCopyTextureInfo {
                    Texture = (WGPUTexture*)texture.GetWgpu<WGPUTexture>().DangerousGetHandle(),
                    MipLevel = (uint)mip, Origin = new() { Z = (uint)layer }, Aspect = WGPUTextureAspect.All
                };
                var dataLayout = new WGPUTexelCopyBufferLayout { BytesPerRow = width * 4, RowsPerImage = height };
                var size = new WGPUExtent3D { Width = width, Height = height, DepthOrArrayLayers = 1 };
                fixed (byte* data = sources[layer].MipLevels.Span[mip].Span) {
                    WgpuUnsafe.wgpuQueueWriteTexture((WGPUQueue*)_gpu.Queue.DangerousGetHandle(), &destination, data,
                        (nuint)(width * height * 4), &dataLayout, &size);
                }
            }
        }
        var viewDescriptor = WGPUTextureViewDescriptor.Default;
        viewDescriptor.Dimension = WGPUTextureViewDimension._2DArray;
        var view = _gpu.Own(Wgpu.CreateTextureView(texture.GetWgpu<WGPUTexture>(), viewDescriptor));
        var samplerDescriptor = WGPUSamplerDescriptor.Default;
        samplerDescriptor.AddressModeU = first.Sampler.AddressU;
        samplerDescriptor.AddressModeV = first.Sampler.AddressV;
        samplerDescriptor.MinFilter = first.Sampler.MinFilter;
        samplerDescriptor.MagFilter = first.Sampler.MagFilter;
        samplerDescriptor.MipmapFilter = first.Sampler.MipFilter;
        samplerDescriptor.LodMaxClamp = first.Sampler.UseMipmaps ? first.MipLevels.Length - 1 : 0;
        return new(texture, view, _gpu.Own(Wgpu.CreateSampler(_gpu.Device, samplerDescriptor)));
    }

    public void Dispose()
    {
        if (!IsStopped) throw new InvalidOperationException("Await material streaming shutdown before disposal.");
        _pending?.Upload?.Dispose();
        foreach (var (Upload, Completion) in _retired) Upload.Dispose();
        foreach (var array in _arrays) array.Residency?.Dispose();
        _gpu.Dispose();
    }
}
