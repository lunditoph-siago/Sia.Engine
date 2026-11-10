using System.Runtime.InteropServices;
using Sia;
using Sia.Graphics.Reactive;
using Sia.Math;
using Sia.RenderGraph;
using Sia.WebGPU;

namespace Sia.Engine.Rendering.Pbr;

internal sealed unsafe partial class PbrView
{
    private readonly List<(RenderGraphTextureKey Key, Entity Texture, Entity View)> _displayTables = [];

    private void InitializeDisplayTransform()
    {
        if (_owner.Settings.DisplayTransform is not { } display) return;
        foreach (var table in display.LookupTables) {
            var size = checked((uint)table.Size);
            var descriptor = WGPUTextureDescriptor.Default;
            descriptor.Dimension = WGPUTextureDimension._3D;
            descriptor.Size = new() { Width = size, Height = size, DepthOrArrayLayers = size };
            descriptor.Format = WGPUTextureFormat.RGBA32Float;
            descriptor.Usage = WGPUTextureUsage.TextureBinding | WGPUTextureUsage.CopyDst;
            var count = checked((int)(size * size * size));
            var texture = _gpu.Texture(descriptor, checked((ulong)count * 16));
            var viewDescriptor = WGPUTextureViewDescriptor.Default;
            viewDescriptor.Dimension = WGPUTextureViewDimension._3D;
            var view = _gpu.Own(Wgpu.CreateTextureView(texture.GetWgpu<WGPUTexture>(), viewDescriptor));
            var pixels = new float4[count]; var rgb = table.Rgb32F;
            for (var index = 0; index < count; index++)
                pixels[index] = new(rgb[index * 3], rgb[index * 3 + 1], rgb[index * 3 + 2], 1);
            var layout = new WGPUTexelCopyBufferLayout { BytesPerRow = checked(size * 16), RowsPerImage = size };
            Wgpu.WriteTexture<float4>(_gpu.Queue, texture.GetWgpu<WGPUTexture>(), pixels, layout, descriptor.Size);
            _displayTables.Add((new(_prefix + "display-table/" + _displayTables.Count), texture, view));
        }
    }

    private Entity DisplayGroup(GpuResources resources, Entity hdrView)
    {
        var entries = new List<WGPUBindGroupEntry> {
            GpuBinding.Buffer(0, _outputUniform),
            GpuBinding.Texture(1, hdrView.GetWgpu<WGPUTextureView>()),
            GpuBinding.Sampler(2, _sampler)
        };
        for (var index = 0; index < _displayTables.Count; index++)
            entries.Add(GpuBinding.Texture((uint)index + 3, _displayTables[index].View.GetWgpu<WGPUTextureView>()));
        return GpuBinding.Group(resources, _owner.Pipelines.OutputLayout, CollectionsMarshal.AsSpan(entries));
    }

    private RenderGraphPassDeclarationBuilder ReadDisplayTransform(RenderGraphPassDeclarationBuilder pass)
    {
        foreach (var table in _displayTables) pass.Read(table.Key, RenderGraphTextureUsage.TextureBinding);
        return pass;
    }
}
