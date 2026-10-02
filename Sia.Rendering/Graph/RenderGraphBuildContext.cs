using Sia.Graphics.Reactive;
using Sia.Reactive;
using Sia.RenderGraph;
using Sia.WebGPU;

namespace Sia.Engine.Rendering;

public delegate void RenderGraphBranchBuilder<T>(in T dependencies, ref RenderGraphBuildContext graph)
    where T : struct;

public ref struct RenderGraphBuildContext
{
    private readonly record struct BranchProps<T>(
        WgpuRenderGraphRegistry Registry,
        T Dependencies,
        RenderGraphBranchBuilder<T> Build) where T : struct;

    private Hooks _hooks;
    private readonly WgpuRenderGraphRegistry _registry;
    private List<(string Key, ReactiveNode Value)>? _branches;

    public RenderGraphBuildContext(
        ref Hooks hooks,
        WgpuRenderGraphRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _hooks = hooks;
        _registry = registry;
        _branches = null;
    }

    public void UseBranch<T>(string key, bool first, in T dependencies, RenderGraphBranchBuilder<T> build)
        where T : struct
    {
        ArgumentNullException.ThrowIfNull(build);
        var props = new BranchProps<T>(_registry, dependencies, build);
        var component = Reactive.Reactive.Component<BranchProps<T>>(BuildBranch, props);
        (_branches ??= []).Add((key, Reactive.Reactive.Either(first, component, component)));
    }

    public ReactiveNode Complete() => Reactive.Reactive.ForEach<string, ReactiveNode, ComponentTerm<ReactiveNode>>(
        static (in node) => Reactive.Reactive.Component<ReactiveNode>(
            static (in child, ref _) => child, node),
        System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_branches ?? []));

    private static ReactiveNode BuildBranch<T>(in BranchProps<T> props, ref Hooks hooks)
        where T : struct
    {
        var graph = new RenderGraphBuildContext(ref hooks, props.Registry);
        props.Build(props.Dependencies, ref graph);
        return graph.Complete();
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

    public void UsePass<TDependencies>(
        RenderGraphPassKey key,
        string name,
        in TDependencies dependencies,
        RenderGraphPassDeclaration<TDependencies> declaration,
        WgpuReactiveRenderGraphPassHandler handler,
        RenderGraphPassKind kind = RenderGraphPassKind.Render)
        where TDependencies : struct, IEquatable<TDependencies>
    {
        _hooks.UseRenderGraphPass(_registry, key, name, in dependencies, declaration, kind);
        _hooks.UseWgpuRenderGraphPassHandler(_registry, key, handler);
    }

    public void UseComputePass(
        RenderGraphPassKey key,
        string name,
        RenderGraphPassDeclaration declaration,
        WgpuReactiveRenderGraphPassHandler handler) =>
        UsePass(key, name, declaration, handler, RenderGraphPassKind.Compute);

    public void UseComputePass<TDependencies>(
        RenderGraphPassKey key,
        string name,
        in TDependencies dependencies,
        RenderGraphPassDeclaration<TDependencies> declaration,
        WgpuReactiveRenderGraphPassHandler handler)
        where TDependencies : struct, IEquatable<TDependencies> =>
        UsePass(key, name, in dependencies, declaration, handler, RenderGraphPassKind.Compute);
}
