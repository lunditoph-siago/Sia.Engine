using System.Runtime.InteropServices;
using Sia;
using Sia.Engine.Mesh;
using Sia.Graphics.Reactive;
using Sia.Math;
using Sia.RenderGraph;
using Sia.WebGPU;

namespace Sia.Engine.Rendering.Pbr;

public sealed unsafe class PbrTransparentScene
{
    private readonly World _world;
    private readonly Entity _device;
    private readonly Entity _queue;
    private readonly Entity _cameraLayout;
    private readonly Entity _pipeline;
    private readonly Draw[] _draws;
    internal bool HasTransmission { get; }
    private readonly List<(RenderGraphBufferKey Key, Entity Buffer, RenderGraphBufferUsage Usage)> _buffers = [];
    private readonly List<VisibilityPbrFeature.MaterialTextureGpu> _textures = [];

    public PbrTransparentScene(in GpuFrame frame, PbrSceneAsset scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        _world = frame.ResourceWorld; _device = frame.Device; _queue = frame.Queue;
        HasTransmission = scene.Materials.ToArray().Any(material => material.Transmission > 0);
        var acquired = new List<Entity>();
        var device = _device.GetWgpu<WGPUDevice>();
        VisibilityPbrFeature.ValidateTextureCapacity(scene.Materials.ToArray().Where(material => material.AlphaBlend).ToArray(), Wgpu.GetLimits(device));
        try {
            var cameraEntry = UniformLayout(0, 144);
            _cameraLayout = Layout(HasTransmission ? [cameraEntry, SceneTextureLayout(1, false), SceneTextureLayout(2, true)] : [cameraEntry]);
            var entries = new WGPUBindGroupLayoutEntry[11];
            entries[0] = UniformLayout(0, 192);
            for (uint i = 0; i < 5; i++) {
                entries[1 + i * 2] = WGPUBindGroupLayoutEntry.Default;
                entries[1 + i * 2].Binding = 1 + i * 2;
                entries[1 + i * 2].Visibility = WGPUShaderStage.Fragment;
                entries[1 + i * 2].Texture.SampleType = WGPUTextureSampleType.Float;
                entries[1 + i * 2].Texture.ViewDimension = WGPUTextureViewDimension._2DArray;
                entries[2 + i * 2] = WGPUBindGroupLayoutEntry.Default;
                entries[2 + i * 2].Binding = 2 + i * 2;
                entries[2 + i * 2].Visibility = WGPUShaderStage.Fragment;
                entries[2 + i * 2].Sampler.Type = WGPUSamplerBindingType.Filtering;
            }
            var materialLayout = Layout(entries);
            var lighting = Own(PbrLightingBindGroupLayout.Create(device));
            var ibl = Own(PbrIblBindGroupLayout.Create(device));
            var layouts = stackalloc WGPUBindGroupLayout*[4];
            layouts[0] = (WGPUBindGroupLayout*)_cameraLayout.GetWgpu<WGPUBindGroupLayout>().DangerousGetHandle();
            layouts[1] = (WGPUBindGroupLayout*)lighting.GetWgpu<WGPUBindGroupLayout>().DangerousGetHandle();
            layouts[2] = (WGPUBindGroupLayout*)ibl.GetWgpu<WGPUBindGroupLayout>().DangerousGetHandle();
            layouts[3] = (WGPUBindGroupLayout*)materialLayout.GetWgpu<WGPUBindGroupLayout>().DangerousGetHandle();
            var pipelineLayout = Own(Wgpu.CreatePipelineLayout(device, new WGPUPipelineLayoutDescriptor {
                BindGroupLayoutCount = 4, BindGroupLayouts = layouts
            }));
            var shader = Own(Wgpu.CreateWgslShaderModule(device, PbrShaderSource.LoadTransparentPbr(HasTransmission), "pbr-transparent"));
            _pipeline = Own(CreatePipeline(device, shader.GetWgpu<WGPUShaderModule>(), pipelineLayout.GetWgpu<WGPUPipelineLayout>()));
            var meshes = new Dictionary<int, (Entity Vertices, Entity Indices, uint Count, float3 Center)>();
            var maps = new Dictionary<PbrTextureData, VisibilityPbrFeature.MaterialTextureGpu>(ReferenceEqualityComparer.Instance);
            var draws = new List<Draw>();
            foreach (var instance in scene.Instances.Span) {
                var material = scene.Materials.Span[instance.Material];
                if (!material.AlphaBlend) { continue; }
                if (!meshes.TryGetValue(instance.Geometry, out var mesh)) {
                    var data = scene.Geometry.Span[instance.Geometry].Build.Tree.CopyFinestGeometry().Geometry;
                    mesh = (Upload<MeshVertex>(data.Vertices, WGPUBufferUsage.Vertex, RenderGraphBufferUsage.Vertex),
                        Upload<uint>(data.Indices, WGPUBufferUsage.Index, RenderGraphBufferUsage.Index),
                        (uint)data.Indices.Length, (data.Bounds.Min + data.Bounds.Max) * .5f);
                    meshes.Add(instance.Geometry, mesh);
                }
                var p = material.Parameters;
                var uniform = Upload<MaterialGpu>([new(instance.Transform, math.transpose(math.inverse(instance.Transform)),
                    new(p.BaseColor, material.Opacity), new(p.EmissiveColor * p.EmissiveStrength, 0),
                    new(p.Metallic, p.Roughness, material.NormalScale, material.OcclusionStrength),
                    new(material.DoubleSided ? 1 : 0, material.Normal is null ? 0 : 1, material.Transmission, material.Thickness))],
                    WGPUBufferUsage.Uniform, RenderGraphBufferUsage.Uniform);
                var bindings = new WGPUBindGroupEntry[11];
                bindings[0] = BufferEntry(0, uniform);
                var sources = VisibilityPbrFeature.MaterialMaps(material);
                for (uint i = 0; i < sources.Length; i++) {
                    if (!maps.TryGetValue(sources[i], out var texture)) {
                        texture = VisibilityPbrFeature.CreateMaterialTexture(_world, device, _queue.GetWgpu<WGPUQueue>(),
                            [sources[i]], maps.Count, acquired);
                        texture = texture with { Key = new($"transparent-texture-{maps.Count}") };
                        maps.Add(sources[i], texture); _textures.Add(texture);
                    }
                    bindings[1 + i * 2] = WGPUBindGroupEntry.Default;
                    bindings[1 + i * 2].Binding = 1 + i * 2;
                    bindings[1 + i * 2].TextureView = (WGPUTextureView*)texture.View.GetWgpu<WGPUTextureView>().DangerousGetHandle();
                    bindings[2 + i * 2] = WGPUBindGroupEntry.Default;
                    bindings[2 + i * 2].Binding = 2 + i * 2;
                    bindings[2 + i * 2].Sampler = (WGPUSampler*)texture.Sampler.GetWgpu<WGPUSampler>().DangerousGetHandle();
                }
                draws.Add(new(mesh.Vertices, mesh.Indices, mesh.Count, Own(Group(device, materialLayout, bindings)),
                    math.mul(instance.Transform, new float4(mesh.Center, 1)).xyz));
            }
            _draws = draws.ToArray();
        }
        catch { for (var i = acquired.Count - 1; i >= 0; i--) { acquired[i].Destroy(); } throw; }

        Entity Own<T>(WgpuHandle<T> handle) where T : unmanaged
        {
            var entity = _world.OwnWgpu(handle); acquired.Add(entity); return entity;
        }
        Entity Layout(ReadOnlySpan<WGPUBindGroupLayoutEntry> entries)
        {
            fixed (WGPUBindGroupLayoutEntry* pointer = entries) {
                return Own(Wgpu.CreateBindGroupLayout(device, new WGPUBindGroupLayoutDescriptor { EntryCount = (nuint)entries.Length, Entries = pointer }));
            }
        }
        Entity Upload<T>(ReadOnlySpan<T> values, WGPUBufferUsage usage, RenderGraphBufferUsage graphUsage) where T : unmanaged
        {
            var entity = Own(Wgpu.CreateBuffer(device, new WGPUBufferDescriptor {
                Size = (ulong)values.Length * (ulong)sizeof(T), Usage = usage | WGPUBufferUsage.CopyDst
            }));
            Wgpu.WriteBuffer(_queue.GetWgpu<WGPUQueue>(), entity.GetWgpu<WGPUBuffer>(), 0, values);
            _buffers.Add((new($"transparent-buffer-{_buffers.Count}"), entity, graphUsage));
            return entity;
        }
    }

    internal void Prepare(in RenderFeatureContext<RenderFrameContext> context, PbrExtractedView extracted)
    {
        if (context.Frame.Frame.ResourceWorld != _world || context.Frame.Frame.Device != _device || context.Frame.Frame.Queue != _queue) {
            throw new InvalidOperationException("The transparent scene belongs to a different resource world/device/queue.");
        }
        var view = context.View.PersistentResources.GetOrAdd(() => new View(this));
        if (view.Owner != this) { throw new InvalidOperationException("A view cannot share different transparent scenes."); }
        var camera = extracted.CameraMatrices;
        Wgpu.WriteBuffer<CameraGpu>(_queue.GetWgpu<WGPUQueue>(), view.Camera.GetWgpu<WGPUBuffer>(), 0,
            [new(camera.ViewProj, new(camera.WorldPosition, 1), math.inverse(camera.ViewProj))]);
        view.Width = (uint)extracted.Viewport.Width; view.Height = (uint)extracted.Viewport.Height;
        view.Sort(camera.WorldPosition);
    }

    internal View Import(ref RenderGraphBuildContext graph, in RenderFeatureContext<RenderFrameContext> context)
    {
        var view = context.View.PersistentResources.GetRequired<View>();
        ImportBuffer(view.CameraKey, view.Camera, RenderGraphBufferUsage.Uniform, ref graph);
        foreach (var buffer in _buffers) { ImportBuffer(buffer.Key, buffer.Buffer, buffer.Usage, ref graph); }
        foreach (var texture in _textures) {
            var info = Wgpu.GetTextureInfo(texture.Texture.GetWgpu<WGPUTexture>());
            graph.UseImportedTexture(texture.Key, new RenderGraphTextureDescriptor(texture.Key.ToString(),
                texture.Srgb ? RenderGraphTextureFormat.RGBA8UnormSrgb : RenderGraphTextureFormat.RGBA8Unorm,
                info.Size.Width, info.Size.Height, mipLevelCount: info.MipLevelCount, usage: RenderGraphTextureUsage.TextureBinding));
            graph.BindImportedTexture(texture.Key, texture.Texture.GetWgpu<WGPUTexture>());
        }
        return view;
    }

    private static void ImportBuffer(RenderGraphBufferKey key, Entity buffer, RenderGraphBufferUsage usage, ref RenderGraphBuildContext graph)
    {
        graph.UseImportedBuffer(key, new RenderGraphBufferDescriptor(key.ToString(), Wgpu.GetBufferSize(buffer.GetWgpu<WGPUBuffer>()), usage));
        graph.BindImportedBuffer(key, buffer.GetWgpu<WGPUBuffer>());
    }

    internal sealed class View
    {
        // Preserve transparency order while amortizing common state. Translation
        // rebuilds only batches whose ordered draws change; camera turns reuse all.
        private const int DrawsPerBundle = 32;
        public PbrTransparentScene Owner { get; }
        public Entity Camera { get; }
        public Entity Group { get; private set; }
        public uint Width { get; set; }
        public uint Height { get; set; }
        private WgpuHandle<WGPUTextureView> _color, _depth;
        private readonly int[] _order;
        private readonly float[] _distances;
        private readonly IComparer<int> _compare;
        private readonly nint[] _orderedBundles;
        private readonly int[] _recordedOrder;
        private float3? _eye;
        private bool _orderChanged = true;
        private readonly Entity[] _bundles;
        private (Entity Camera, Entity Lighting, Entity Ibl)? _bindings;
        public RenderGraphBufferKey CameraKey { get; } = new("transparent-camera");
        public View(PbrTransparentScene owner)
        {
            Owner = owner;
            _order = Enumerable.Range(0, owner._draws.Length).ToArray();
            _distances = new float[_order.Length];
            _recordedOrder = new int[_order.Length];
            Array.Fill(_recordedOrder, -1);
            _bundles = new Entity[(_order.Length + DrawsPerBundle - 1) / DrawsPerBundle];
            _orderedBundles = new nint[_bundles.Length];
            _compare = Comparer<int>.Create((left, right) => {
                var distance = _distances[right].CompareTo(_distances[left]);
                // Match OrderByDescending's source-order tie break.
                return distance != 0 ? distance : left.CompareTo(right);
            });
            Camera = owner._world.OwnWgpu(Wgpu.CreateBuffer(owner._device.GetWgpu<WGPUDevice>(),
                new WGPUBufferDescriptor { Size = 144, Usage = WGPUBufferUsage.Uniform | WGPUBufferUsage.CopyDst }));
            try { if (!owner.HasTransmission) { Group = owner._world.OwnWgpu(PbrTransparentScene.Group(owner._device.GetWgpu<WGPUDevice>(), owner._cameraLayout, [BufferEntry(0, Camera)])); } }
            catch { Camera.Destroy(); throw; }
        }
        public void Sort(float3 eye)
        {
            if (_eye is { } previous && previous.Equals(eye)) { return; }
            for (var i = 0; i < _distances.Length; i++) { _distances[i] = math.lengthsq(Owner._draws[i].Center - eye); }
            Array.Sort(_order, _compare);
            _eye = eye;
            _orderChanged = true;
        }
        public void BindScene(WgpuHandle<WGPUTextureView> color, WgpuHandle<WGPUTextureView> depth)
        {
            if (Group.IsValid && color.DangerousGetHandle() == _color.DangerousGetHandle()
                && depth.DangerousGetHandle() == _depth.DangerousGetHandle()) { return; }
            var next = PbrTextureBindGroups.Create(Owner._world, Owner._device.GetWgpu<WGPUDevice>(), Owner._cameraLayout,
                [BufferEntry(0, Camera), new WGPUBindGroupEntry { Binding = 1, TextureView = (WGPUTextureView*)color.DangerousGetHandle() },
                    new WGPUBindGroupEntry { Binding = 2, TextureView = (WGPUTextureView*)depth.DangerousGetHandle() }], color, depth);
            if (Group.IsValid) { Group.Destroy(); }
            Group = next; _color = color; _depth = depth;
        }
        public void Declare(RenderGraphPassDeclarationBuilder declaration)
        {
            declaration.Read(CameraKey, RenderGraphBufferUsage.Uniform);
            foreach (var buffer in Owner._buffers) { declaration.Read(buffer.Key, buffer.Usage); }
            foreach (var texture in Owner._textures) { declaration.Read(texture.Key, RenderGraphTextureUsage.TextureBinding); }
        }
        public void Render(WgpuHandle<WGPURenderPassEncoder> pass, PbrViewState lighting)
        {
            var bindings = (Group, lighting.ForwardLightingBindGroup, lighting.IblBindGroup);
            var bindingsChanged = _bindings != bindings;
            if (bindingsChanged || _orderChanged) {
                for (var i = 0; i < _bundles.Length; i++) {
                    var start = i * DrawsPerBundle;
                    var count = System.Math.Min(DrawsPerBundle, _order.Length - start);
                    var order = _order.AsSpan(start, count);
                    var recorded = _recordedOrder.AsSpan(start, count);
                    if (!bindingsChanged && order.SequenceEqual(recorded)) { continue; }
                    var next = CreateBundle(order, lighting);
                    if (_bundles[i].IsValid) { _bundles[i].Destroy(); }
                    _bundles[i] = next;
                    _orderedBundles[i] = next.GetWgpu<WGPURenderBundle>().DangerousGetHandle();
                    order.CopyTo(recorded);
                }
                _bindings = bindings;
                _orderChanged = false;
            }
            fixed (nint* bundles = _orderedBundles) {
                WgpuUnsafe.wgpuRenderPassEncoderExecuteBundles((WGPURenderPassEncoder*)pass.DangerousGetHandle(),
                    (nuint)_orderedBundles.Length, (WGPURenderBundle**)bundles);
            }
        }
        private Entity CreateBundle(ReadOnlySpan<int> order, PbrViewState lighting)
        {
            var format = WGPUTextureFormat.RGBA16Float;
            var descriptor = WGPURenderBundleEncoderDescriptor.Default;
            descriptor.ColorFormatCount = 1; descriptor.ColorFormats = &format;
            descriptor.DepthStencilFormat = WGPUTextureFormat.Depth32Float;
            descriptor.DepthReadOnly = 1; descriptor.StencilReadOnly = 1;
            var encoder = WgpuUnsafe.wgpuDeviceCreateRenderBundleEncoder((WGPUDevice*)Owner._device.GetWgpu<WGPUDevice>().DangerousGetHandle(), &descriptor);
            if (encoder == null) { throw new WgpuException("Could not create transparent render bundle encoder."); }
            try {
                WgpuUnsafe.wgpuRenderBundleEncoderSetPipeline(encoder, (WGPURenderPipeline*)Owner._pipeline.GetWgpu<WGPURenderPipeline>().DangerousGetHandle());
                WgpuUnsafe.wgpuRenderBundleEncoderSetBindGroup(encoder, 0, (WGPUBindGroup*)Group.GetWgpu<WGPUBindGroup>().DangerousGetHandle(), 0, null);
                WgpuUnsafe.wgpuRenderBundleEncoderSetBindGroup(encoder, 1, (WGPUBindGroup*)lighting.ForwardLightingBindGroup.GetWgpu<WGPUBindGroup>().DangerousGetHandle(), 0, null);
                WgpuUnsafe.wgpuRenderBundleEncoderSetBindGroup(encoder, 2, (WGPUBindGroup*)lighting.IblBindGroup.GetWgpu<WGPUBindGroup>().DangerousGetHandle(), 0, null);
                foreach (var index in order) {
                    var draw = Owner._draws[index];
                    WgpuUnsafe.wgpuRenderBundleEncoderSetBindGroup(encoder, 3, (WGPUBindGroup*)draw.Group.GetWgpu<WGPUBindGroup>().DangerousGetHandle(), 0, null);
                    var vertices = draw.Vertices.GetWgpu<WGPUBuffer>();
                    var indices = draw.Indices.GetWgpu<WGPUBuffer>();
                    WgpuUnsafe.wgpuRenderBundleEncoderSetVertexBuffer(encoder, 0, (WGPUBuffer*)vertices.DangerousGetHandle(), 0, Wgpu.GetBufferSize(vertices));
                    WgpuUnsafe.wgpuRenderBundleEncoderSetIndexBuffer(encoder, (WGPUBuffer*)indices.DangerousGetHandle(), WGPUIndexFormat.Uint32, 0, Wgpu.GetBufferSize(indices));
                    WgpuUnsafe.wgpuRenderBundleEncoderDrawIndexed(encoder, draw.Count, 1, 0, 0, 0);
                }
                var bundle = WgpuUnsafe.wgpuRenderBundleEncoderFinish(encoder, null);
                if (bundle == null) { throw new WgpuException("Could not finish transparent render bundle."); }
                return Owner._world.OwnWgpu(new WgpuHandle<WGPURenderBundle>((nint)bundle));
            }
            finally { WgpuUnsafe.wgpuRenderBundleEncoderRelease(encoder); }
        }
    }

    private static WGPUBindGroupLayoutEntry UniformLayout(uint binding, ulong size)
    {
        var entry = WGPUBindGroupLayoutEntry.Default;
        entry.Binding = binding; entry.Visibility = WGPUShaderStage.Vertex | WGPUShaderStage.Fragment;
        entry.Buffer.Type = WGPUBufferBindingType.Uniform; entry.Buffer.MinBindingSize = size; return entry;
    }
    private static WGPUBindGroupLayoutEntry SceneTextureLayout(uint binding, bool depth)
    {
        var entry = WGPUBindGroupLayoutEntry.Default;
        entry.Binding = binding; entry.Visibility = WGPUShaderStage.Fragment;
        entry.Texture.SampleType = depth ? WGPUTextureSampleType.UnfilterableFloat : WGPUTextureSampleType.Float;
        entry.Texture.ViewDimension = WGPUTextureViewDimension._2D;
        return entry;
    }
    private static WGPUBindGroupEntry BufferEntry(uint binding, Entity buffer) => new() {
        Binding = binding, Buffer = (WGPUBuffer*)buffer.GetWgpu<WGPUBuffer>().DangerousGetHandle(),
        Size = Wgpu.GetBufferSize(buffer.GetWgpu<WGPUBuffer>())
    };
    private static WgpuHandle<WGPUBindGroup> Group(WgpuHandle<WGPUDevice> device, Entity layout, ReadOnlySpan<WGPUBindGroupEntry> entries)
    {
        fixed (WGPUBindGroupEntry* pointer = entries) {
            return Wgpu.CreateBindGroup(device, new WGPUBindGroupDescriptor {
                Layout = (WGPUBindGroupLayout*)layout.GetWgpu<WGPUBindGroupLayout>().DangerousGetHandle(),
                EntryCount = (nuint)entries.Length, Entries = pointer
            });
        }
    }
    private static WgpuHandle<WGPURenderPipeline> CreatePipeline(WgpuHandle<WGPUDevice> device,
        WgpuHandle<WGPUShaderModule> shader, WgpuHandle<WGPUPipelineLayout> layout)
    {
        var attributes = stackalloc WGPUVertexAttribute[4];
        attributes[0] = new() { Format = WGPUVertexFormat.Float32x3, Offset = 0, ShaderLocation = 0 };
        attributes[1] = new() { Format = WGPUVertexFormat.Float32x3, Offset = MeshVertex.NormalOffset, ShaderLocation = 1 };
        attributes[2] = new() { Format = WGPUVertexFormat.Float32x2, Offset = MeshVertex.UVOffset, ShaderLocation = 2 };
        attributes[3] = new() { Format = WGPUVertexFormat.Float32x4, Offset = MeshVertex.TangentOffset, ShaderLocation = 3 };
        var vertices = WGPUVertexBufferLayout.Default;
        vertices.ArrayStride = MeshVertex.Stride; vertices.StepMode = WGPUVertexStepMode.Vertex;
        vertices.AttributeCount = 4; vertices.Attributes = attributes;
        var blend = WGPUBlendState.Default;
        blend.Color.SrcFactor = WGPUBlendFactor.SrcAlpha; blend.Color.DstFactor = WGPUBlendFactor.OneMinusSrcAlpha;
        blend.Alpha.SrcFactor = WGPUBlendFactor.One; blend.Alpha.DstFactor = WGPUBlendFactor.OneMinusSrcAlpha;
        var target = WGPUColorTargetState.Default;
        target.Format = WGPUTextureFormat.RGBA16Float; target.Blend = &blend;
        var depth = WGPUDepthStencilState.Default;
        depth.Format = WGPUTextureFormat.Depth32Float; depth.DepthWriteEnabled = WGPUOptionalBool.False;
        depth.DepthCompare = WGPUCompareFunction.LessEqual;
        fixed (byte* vertex = "vertex"u8)
        fixed (byte* fragment = "fragment"u8) {
            var fs = WGPUFragmentState.Default;
            fs.Module = (WGPUShaderModule*)shader.DangerousGetHandle(); fs.EntryPoint = new() { Data = fragment, Length = 8 };
            fs.TargetCount = 1; fs.Targets = &target;
            var descriptor = WGPURenderPipelineDescriptor.Default;
            descriptor.Layout = (WGPUPipelineLayout*)layout.DangerousGetHandle();
            descriptor.Vertex = WGPUVertexState.Default;
            descriptor.Vertex.Module = fs.Module; descriptor.Vertex.EntryPoint = new() { Data = vertex, Length = 6 };
            descriptor.Vertex.BufferCount = 1; descriptor.Vertex.Buffers = &vertices;
            descriptor.Fragment = &fs; descriptor.DepthStencil = &depth;
            descriptor.Primitive = WGPUPrimitiveState.Default; descriptor.Primitive.CullMode = WGPUCullMode.None;
            descriptor.Multisample = WGPUMultisampleState.Default;
            return Wgpu.CreateRenderPipeline(device, descriptor);
        }
    }
    internal sealed record Draw(Entity Vertices, Entity Indices, uint Count, Entity Group, float3 Center);
    [StructLayout(LayoutKind.Sequential)] private readonly record struct CameraGpu(float4x4 Matrix, float4 Eye, float4x4 Inverse);
    [StructLayout(LayoutKind.Sequential)] private readonly record struct MaterialGpu(float4x4 Transform, float4x4 Normal,
        float4 Color, float4 Emission, float4 Factors, float4 Flags);
}
