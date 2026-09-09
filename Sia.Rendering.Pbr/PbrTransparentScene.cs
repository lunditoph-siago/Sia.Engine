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
    private readonly List<(RenderGraphBufferKey Key, Entity Buffer, RenderGraphBufferUsage Usage)> _buffers = [];
    private readonly List<VisibilityPbrFeature.MaterialTextureGpu> _textures = [];

    public PbrTransparentScene(in GpuFrame frame, PbrSceneAsset scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        _world = frame.ResourceWorld; _device = frame.Device; _queue = frame.Queue;
        var acquired = new List<Entity>();
        var device = _device.GetWgpu<WGPUDevice>();
        VisibilityPbrFeature.ValidateTextureCapacity(scene.Materials.ToArray().Where(material => material.AlphaBlend).ToArray(), Wgpu.GetLimits(device));
        try {
            var cameraEntry = UniformLayout(0, 80);
            _cameraLayout = Layout([cameraEntry]);
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
            var shader = Own(Wgpu.CreateWgslShaderModule(device, PbrShaderSource.LoadTransparentPbr(), "pbr-transparent"));
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
                    new(material.DoubleSided ? 1 : 0, material.Normal is null ? 0 : 1, 0, 0))],
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
            [new(camera.ViewProj, new(camera.WorldPosition, 1))]);
        view.Draws = _draws.OrderByDescending(draw => math.lengthsq(draw.Center - camera.WorldPosition)).ToArray();
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
        public PbrTransparentScene Owner { get; }
        public Entity Camera { get; }
        public Entity Group { get; }
        public Draw[] Draws { get; set; } = [];
        public RenderGraphBufferKey CameraKey { get; } = new("transparent-camera");
        public View(PbrTransparentScene owner)
        {
            Owner = owner;
            Camera = owner._world.OwnWgpu(Wgpu.CreateBuffer(owner._device.GetWgpu<WGPUDevice>(),
                new WGPUBufferDescriptor { Size = 80, Usage = WGPUBufferUsage.Uniform | WGPUBufferUsage.CopyDst }));
            try { Group = owner._world.OwnWgpu(PbrTransparentScene.Group(owner._device.GetWgpu<WGPUDevice>(), owner._cameraLayout, [BufferEntry(0, Camera)])); }
            catch { Camera.Destroy(); throw; }
        }
        public void Declare(RenderGraphPassDeclarationBuilder declaration)
        {
            declaration.Read(CameraKey, RenderGraphBufferUsage.Uniform);
            foreach (var buffer in Owner._buffers) { declaration.Read(buffer.Key, buffer.Usage); }
            foreach (var texture in Owner._textures) { declaration.Read(texture.Key, RenderGraphTextureUsage.TextureBinding); }
        }
        public void Render(WgpuHandle<WGPURenderPassEncoder> pass, PbrViewState lighting)
        {
            Wgpu.SetRenderPipeline(pass, Owner._pipeline.GetWgpu<WGPURenderPipeline>());
            Wgpu.SetBindGroup(pass, 0, Group.GetWgpu<WGPUBindGroup>());
            Wgpu.SetBindGroup(pass, 1, lighting.ForwardLightingBindGroup.GetWgpu<WGPUBindGroup>());
            Wgpu.SetBindGroup(pass, 2, lighting.IblBindGroup.GetWgpu<WGPUBindGroup>());
            foreach (var draw in Draws) {
                Wgpu.SetBindGroup(pass, 3, draw.Group.GetWgpu<WGPUBindGroup>());
                Wgpu.SetVertexBuffer(pass, 0, draw.Vertices.GetWgpu<WGPUBuffer>());
                Wgpu.SetIndexBuffer(pass, draw.Indices.GetWgpu<WGPUBuffer>(), WGPUIndexFormat.Uint32);
                Wgpu.DrawIndexed(pass, draw.Count);
            }
        }
    }

    private static WGPUBindGroupLayoutEntry UniformLayout(uint binding, ulong size)
    {
        var entry = WGPUBindGroupLayoutEntry.Default;
        entry.Binding = binding; entry.Visibility = WGPUShaderStage.Vertex | WGPUShaderStage.Fragment;
        entry.Buffer.Type = WGPUBufferBindingType.Uniform; entry.Buffer.MinBindingSize = size; return entry;
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
    [StructLayout(LayoutKind.Sequential)] private readonly record struct CameraGpu(float4x4 Matrix, float4 Eye);
    [StructLayout(LayoutKind.Sequential)] private readonly record struct MaterialGpu(float4x4 Transform, float4x4 Normal,
        float4 Color, float4 Emission, float4 Factors, float4 Flags);
}
