using Sia.Graphics.Reactive;
using Sia.Reactive;
using Sia.RenderGraph;
using Sia.WebGPU;

namespace Sia.Engine.Rendering;

public ref struct RenderGraphBuildContext
{
    private Hooks _hooks;
    private readonly WgpuRenderGraphRegistry _registry;

    public RenderGraphBuildContext(
        ref Hooks hooks,
        WgpuRenderGraphRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _hooks = hooks;
        _registry = registry;
    }

    public T UseState<T>(Func<T> factory)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(factory);
        return _hooks.UseRef(factory).Value;
    }

    public void UseBuffer(
        RenderGraphBufferKey key,
        in RenderGraphBufferDescriptor descriptor) =>
        _hooks.UseRenderGraphBuffer(_registry, key, in descriptor);

    public void UseImportedBuffer(
        RenderGraphBufferKey key,
        in RenderGraphBufferDescriptor descriptor) =>
        _hooks.UseImportedRenderGraphBuffer(_registry, key, in descriptor);

    public void BindImportedBuffer(
        RenderGraphBufferKey key,
        WgpuHandle<WGPUBuffer> buffer) =>
        _hooks.UseImportedRenderGraphBufferBinding(_registry, key, buffer);

    public void ExportBuffer(
        RenderGraphBufferKey key,
        RenderGraphBufferUsage usage = RenderGraphBufferUsage.None) =>
        _hooks.UseRenderGraphBufferExport(_registry, key, usage);

    public void UseTexture(
        RenderGraphTextureKey key,
        in RenderGraphTextureDescriptor descriptor) =>
        _hooks.UseRenderGraphTexture(_registry, key, in descriptor);

    public void UseImportedTexture(
        RenderGraphTextureKey key,
        in RenderGraphTextureDescriptor descriptor) =>
        _hooks.UseImportedRenderGraphTexture(_registry, key, in descriptor);

    public void BindImportedTexture(
        RenderGraphTextureKey key,
        WgpuHandle<WGPUTexture> texture) =>
        _hooks.UseImportedRenderGraphTextureBinding(_registry, key, texture);

    public void ExportTexture(
        RenderGraphTextureKey key,
        RenderGraphTextureUsage usage = RenderGraphTextureUsage.None) =>
        _hooks.UseRenderGraphTextureExport(_registry, key, usage);

    public void UsePass(
        RenderGraphPassKey key,
        string name,
        RenderGraphPassDeclaration declaration,
        WgpuReactiveRenderGraphPassHandler handler,
        RenderGraphPassKind kind = RenderGraphPassKind.Render)
    {
        _hooks.UseRenderGraphPass(_registry, key, name, declaration, kind);
        _hooks.UseWgpuRenderGraphPassHandler(_registry, key, handler);
    }

    public void UseComputePass(
        RenderGraphPassKey key,
        string name,
        RenderGraphPassDeclaration declaration,
        WgpuReactiveRenderGraphPassHandler handler) =>
        UsePass(key, name, declaration, handler, RenderGraphPassKind.Compute);
}
