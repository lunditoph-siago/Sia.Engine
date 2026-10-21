using Sia;
using Sia.WebGPU;

namespace Sia.Engine.Rendering.Pbr;

// Rendering owns the root, bounded member metadata slab, coefficient layers and queue lifetime.
internal sealed unsafe class PbrLightmapGpu : IDisposable
{
    private const int MetadataWidth = 256;
    private readonly GpuResources _gpu;
    private readonly Entity _pages, _mapping, _metadata;
    private readonly int _mapWidth;
    private readonly PbrLightmapMetadata _root;
    private readonly PbrLightmapSetData _set;
    private readonly PbrLightmapProductPool _products;
    private readonly PbrLightmapProductStreaming _productStreaming;
    private readonly PbrLightmapCoefficientStreaming _coefficients;
    private readonly PbrLightmapDemand _demand;
    private Task? _stop;
    internal Entity PageView { get; }
    internal Entity MappingView { get; }
    internal Entity MetadataView { get; }
    internal ulong Bytes => _gpu.Bytes;
    internal bool IsStopped => _stop is { IsCompletedSuccessfully: true };
    internal Entity[] Textures => [_pages, _mapping, _metadata];

    internal static ulong RequiredPageBytes(int pages, int slots)
    {
        if (pages is < 1 or > PbrLightmapData.MaximumPages || slots < 1 || slots > pages)
            throw new ArgumentOutOfRangeException(nameof(pages));
        var width = System.Math.Min(256, pages); var height = (pages + width - 1) / width;
        return checked((ulong)slots * PbrLightmapPagePool.SlotBytes + (ulong)width * (ulong)height * sizeof(uint));
    }

    private static int Addresses(PbrLightmapSetData set, PbrLightmapStreamingSettings settings)
    {
        if (settings.PageAddresses is < 1 or > PbrLightmapData.MaximumPages || settings.ResidentPages < 1)
            throw new ArgumentOutOfRangeException(nameof(settings));
        long total = 0; for (var at = 0; at < set.Products.Length; at++) total += set.ProductCounts(at).Pages;
        return (int)System.Math.Min(total, settings.PageAddresses);
    }

    private static int RootTexels(PbrLightmapSetData set, int instances)
        => checked((int)((PbrLightmapMetadata.RequiredBytes(set, instances) / 16 + MetadataWidth - 1) / MetadataWidth) * MetadataWidth);

    internal static ulong RequiredBytes(PbrLightmapSetData set, PbrLightmapStreamingSettings settings, int instances)
    {
        ArgumentNullException.ThrowIfNull(set); ArgumentNullException.ThrowIfNull(settings);
        if (settings.GraphBytes < 16 || settings.GraphBytes % 16 != 0 || settings.GraphBytes > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(settings));
        var addresses = Addresses(set, settings); var texels = checked((ulong)RootTexels(set, instances) + settings.GraphBytes / 16);
        var height = (texels + MetadataWidth - 1) / MetadataWidth;
        return checked(RequiredPageBytes(addresses, System.Math.Min(settings.ResidentPages, addresses)) + height * MetadataWidth * 16);
    }

    internal PbrLightmapGpu(in GpuFrame frame, PbrLightmapInput input, ReadOnlySpan<int> receiverRows, PbrLightmapStreamingSettings settings)
    {
        ArgumentNullException.ThrowIfNull(input); ArgumentNullException.ThrowIfNull(input.Set);
        ArgumentNullException.ThrowIfNull(input.PrepareProduct); ArgumentNullException.ThrowIfNull(input.AcquireProduct);
        ArgumentNullException.ThrowIfNull(input.PreparePage); ArgumentNullException.ThrowIfNull(input.AcquirePage);
        var required = RequiredBytes(input.Set, settings, receiverRows.Length);
        if (required > settings.GpuBytes) throw new ArgumentOutOfRangeException(nameof(settings), "Lighting root/member/pages exceed GPU admission.");
        if (settings.UploadBytesPerFrame < 128) throw new ArgumentOutOfRangeException(nameof(settings), "Lighting upload budget must admit metadata and coefficients.");
        _set = input.Set; var addresses = Addresses(_set, settings); var slots = System.Math.Min(settings.ResidentPages, addresses);
        var fixedCpu = checked(PbrLightmapMetadata.RequiredBytes(_set, receiverRows.Length)
            + PbrLightmapDemand.RequiredBytes(_set, receiverRows.Length) + PbrLightmapPagePool.SlotBytes + (ulong)addresses * sizeof(long));
        if (fixedCpu >= settings.MetadataBytes) throw new ArgumentOutOfRangeException(nameof(settings), "Lighting fixed CPU storage exceeds metadata admission.");
        _root = new(_set, receiverRows, PbrLightmapMetadata.RequiredBytes(_set, receiverRows.Length)); _demand = new(_set, receiverRows);
        var firstGraphTexel = RootTexels(_set, receiverRows.Length);
        var metadataHeight = checked((int)(((ulong)firstGraphTexel + settings.GraphBytes / 16 + MetadataWidth - 1) / MetadataWidth));
        _mapWidth = System.Math.Min(256, addresses); var mapHeight = (addresses + _mapWidth - 1) / _mapWidth;
        _gpu = new(frame, settings.GpuBytes);
        try {
            PbrLightmapCoefficientStreaming? coefficients = null;
            bool Withdraw(StreamRange range, uint budget, out uint uploaded) => coefficients!.Withdraw(range, budget, out uploaded);
            _products = new(_set, firstGraphTexel, (uint)(settings.GraphBytes / 16), (uint)addresses, settings.MetadataBytes - fixedCpu,
                WriteMetadata, WriteProductPointer, Withdraw, () => GpuQueueCompletion.AfterSubmittedWork(_gpu.Queue));
            var metadataUpload = settings.UploadBytesPerFrame / 4;
            _productStreaming = new(_set, _products, metadataUpload, settings.IdleFrames, settings.ProductRequests, settings.ProductDecodedBytes,
                input.PrepareProduct, input.AcquireProduct);
            _coefficients = coefficients = new(_products, addresses, slots, settings.UploadBytesPerFrame - metadataUpload,
                settings.IdleFrames, settings.MaximumRequests, settings.DecodedBytes, input.PreparePage, input.AcquirePage,
                WritePage, WriteMapping, () => GpuQueueCompletion.AfterSubmittedWork(_gpu.Queue));
            if (_gpu.Limits.MaxTextureDimension2D < System.Math.Max(MetadataWidth, _mapWidth)
                || _gpu.Limits.MaxTextureDimension2D < mapHeight || _gpu.Limits.MaxTextureDimension2D < metadataHeight
                || _gpu.Limits.MaxTextureArrayLayers < slots)
                throw new NotSupportedException("Lighting integer textures exceed device dimensions/layers.");
            var binding = WGPUTextureBindingViewDimension.Default; binding.TextureBindingViewDimension = WGPUTextureViewDimension._2DArray;
            var descriptor = WGPUTextureDescriptor.Default; descriptor.NextInChain = &binding.Chain;
            descriptor.Dimension = WGPUTextureDimension._2D; descriptor.Size = new() { Width = 128, Height = 64, DepthOrArrayLayers = (uint)slots };
            descriptor.Format = WGPUTextureFormat.RGBA32Uint; descriptor.Usage = WGPUTextureUsage.TextureBinding | WGPUTextureUsage.CopyDst;
            _pages = _gpu.Texture(descriptor, (ulong)slots * PbrLightmapPagePool.SlotBytes);
            var view = WGPUTextureViewDescriptor.Default; view.Dimension = WGPUTextureViewDimension._2DArray;
            PageView = _gpu.Own(Wgpu.CreateTextureView(_pages.GetWgpu<WGPUTexture>(), view));
            descriptor.NextInChain = null; descriptor.Size = new() { Width = (uint)_mapWidth, Height = (uint)mapHeight, DepthOrArrayLayers = 1 };
            descriptor.Format = WGPUTextureFormat.R32Uint; _mapping = _gpu.Texture(descriptor, (ulong)_mapWidth * (ulong)mapHeight * sizeof(uint));
            MappingView = _gpu.Own(Wgpu.CreateTextureView(_mapping.GetWgpu<WGPUTexture>(), WGPUTextureViewDescriptor.Default));
            descriptor.Size = new() { Width = MetadataWidth, Height = (uint)metadataHeight, DepthOrArrayLayers = 1 };
            descriptor.Format = WGPUTextureFormat.RGBA32Uint; _metadata = _gpu.Texture(descriptor, (ulong)MetadataWidth * (ulong)metadataHeight * 16);
            MetadataView = _gpu.Own(Wgpu.CreateTextureView(_metadata.GetWgpu<WGPUTexture>(), WGPUTextureViewDescriptor.Default));
            WriteMetadata(0, _root.Words);
        }
        catch { _gpu.Dispose(); throw; }
    }

    private void WriteMetadata(int firstTexel, ReadOnlySpan<uint> words)
        => WriteTexture(_metadata, MetadataWidth, 1, 4, firstTexel, words);

    private void WriteTexture(Entity texture, int rowElements, int pixelsPerElement, int wordsPerPixel,
        int firstElement, ReadOnlySpan<uint> words, int layer = 0)
    {
        var wordsPerElement = checked(pixelsPerElement * wordsPerPixel);
        if (words.Length % wordsPerElement != 0) throw new ArgumentException("Incomplete integer texture element.", nameof(words));
        while (!words.IsEmpty) {
            var region = PbrLightmapTextureCopy.Next(rowElements, pixelsPerElement, wordsPerPixel,
                firstElement, words.Length / wordsPerElement);
            var target = new WGPUTexelCopyTextureInfo { Texture = (WGPUTexture*)texture.GetWgpu<WGPUTexture>().DangerousGetHandle(),
                Origin = new() { X = (uint)region.X, Y = (uint)region.Y, Z = (uint)layer }, Aspect = WGPUTextureAspect.All };
            var layout = new WGPUTexelCopyBufferLayout { BytesPerRow = (uint)region.BytesPerRow, RowsPerImage = (uint)region.Height };
            var extent = new WGPUExtent3D { Width = (uint)region.Width, Height = (uint)region.Height, DepthOrArrayLayers = 1 };
            fixed (uint* data = words) WgpuUnsafe.wgpuQueueWriteTexture((WGPUQueue*)_gpu.Queue.DangerousGetHandle(), &target,
                data, (nuint)region.WordCount * sizeof(uint), &layout, &extent);
            firstElement += region.ElementCount; words = words[region.WordCount..];
        }
    }

    private void WriteProductPointer(int product, uint pointer)
    {
        var firstReceiver = (int)_root.Words[5]; Span<uint> header = stackalloc uint[4];
        for (var at = 0; at < _set.Receivers.Length; at++) {
            var binding = _set.Receivers[at]; if (binding.Product != product) continue;
            header[0] = (uint)binding.Receiver.Resolution; header[1] = pointer; header[2] = (uint)binding.ProductReceiver; header[3] = (uint)product;
            WriteMetadata(firstReceiver + at * PbrLightmapMetadata.RootReceiverTexels, header);
        }
    }

    private void WritePage(int slot, int edge, int firstTexel, ReadOnlySpan<uint> words)
        => WriteTexture(_pages, edge, 2, 4, firstTexel, words, slot);

    private void WriteMapping(int page, int slotPlusOne)
    {
        ReadOnlySpan<uint> value = [(uint)slotPlusOne];
        WriteTexture(_mapping, _mapWidth, 1, 1, page, value);
    }

    internal Task StopAsync() => _stop ??= Task.WhenAll(_productStreaming.StopAsync(), _coefficients.StopAsync());
    internal void BeginFrame() { _coefficients.BeginFrame(); _productStreaming.BeginFrame(); _demand.BeginFrame(); }
    internal void Demand(uint instance, float pixels) => _demand.Demand(instance, pixels);
    internal void EndFrame()
    {
        _demand.ApplyProducts(_productStreaming.Demand); _demand.ApplyPages(_coefficients.Demand);
        _productStreaming.EndFrame(); _coefficients.EndFrame();
    }
    public void Dispose()
    {
        if (!IsStopped) throw new InvalidOperationException("Stop lighting reads and queue work before owner-side disposal.");
        _productStreaming.Dispose(); _gpu.Dispose();
    }
}
