using Sia;
using Sia.Graphics.Reactive;
using Sia.Math;
using Sia.RenderGraph;
using Sia.WebGPU;

namespace Sia.Engine.Rendering.Pbr;

internal sealed unsafe partial class PbrView
{
    private PbrGpuSelection? _selection;
    private RenderGraphBufferKey _streamWorkKey, _streamArgsKey, _streamFeedbackKey;

    internal PbrGpuSelection? Selection => _selection;

    private void InitializeGpuSelection()
    {
        if (_owner.Scene.Streaming?.Hierarchy is not { } hierarchy) return;
        _selection = new(_gpuFrame, hierarchy, _owner.Pipelines.StreamWorkLayout, _owner.Settings.ViewBytes - _gpu.Bytes);
        _streamWorkKey = new(_prefix + "stream-work");
        _streamArgsKey = new(_prefix + "stream-args");
        _streamFeedbackKey = new(_prefix + "stream-feedback");
        _sceneBuffers.Add((_streamWorkKey, _selection.Work, RenderGraphBufferUsage.Storage));
        _sceneBuffers.Add((_streamArgsKey, _selection.Arguments, RenderGraphBufferUsage.Storage | RenderGraphBufferUsage.Indirect | RenderGraphBufferUsage.CopySource));
        _sceneBuffers.Add((_streamFeedbackKey, _selection.Feedback, RenderGraphBufferUsage.Storage | RenderGraphBufferUsage.CopySource));
        _sceneBuffers.Add((new(_prefix + "stream-nodes"), hierarchy.Nodes, RenderGraphBufferUsage.Storage));
        _sceneBuffers.Add((new(_prefix + "stream-parts"), hierarchy.Parts, RenderGraphBufferUsage.Storage));
        _sceneBuffers.Add((new(_prefix + "stream-residency"), hierarchy.Residency, RenderGraphBufferUsage.Storage));
        var at = 0;
        foreach (var configuration in _selection.Configurations)
            _sceneBuffers.Add((new(_prefix + "stream-config/" + at++), configuration, RenderGraphBufferUsage.Uniform));
        for (var i = 0; i < 3; i++)
            _sceneBuffers.Add((new(_prefix + "stream-readback/" + i), _selection.Readback(i), RenderGraphBufferUsage.CopyDestination));
    }

    private RenderGraphPassDeclarationBuilder ReadStreamInputs(RenderGraphPassDeclarationBuilder pass)
    {
        foreach (var (bufferKey, buffer, usage) in _sceneBuffers) {
            var key = bufferKey.Value;
            if (key.StartsWith(_prefix + "stream-config/", StringComparison.Ordinal)
                || key == _prefix + "stream-nodes" || key == _prefix + "stream-parts" || key == _prefix + "stream-residency"
                || buffer == _owner.Scene.Instances)
                pass.Read(bufferKey, usage);
        }
        return pass;
    }

    private RenderGraphPassDeclarationBuilder WriteStreamWork(RenderGraphPassDeclarationBuilder pass)
        => ReadStreamInputs(pass).ReadWrite(_streamWorkKey, RenderGraphBufferUsage.Storage)
            .ReadWrite(_streamArgsKey, RenderGraphBufferUsage.Storage | RenderGraphBufferUsage.Indirect)
            .ReadWrite(_streamFeedbackKey, RenderGraphBufferUsage.Storage);

    private RenderGraphPassDeclarationBuilder ReadStreamWork(RenderGraphPassDeclarationBuilder pass)
        => _selection is null ? pass : ReadStreamInputs(pass).Read(_streamWorkKey, RenderGraphBufferUsage.Storage)
            .Read(_streamArgsKey, RenderGraphBufferUsage.Indirect);

    private void BuildStreamReset(ref RenderGraphBuildContext graph, Dependencies dependency)
    {
        if (_selection is null) return;
        graph.UsePass(new(_prefix + "stream-reset"), "pbr-stream-feedback-reset", dependency,
            static (in d, p) => d.View.ReadStreamInputs(p)
                .Write(d.View._streamFeedbackKey, RenderGraphBufferUsage.Storage),
            context => _selection.Reset(context.CommandEncoder), RenderGraphPassKind.Compute);
    }

    private void BuildStreamSelection(ref RenderGraphBuildContext graph, Dependencies dependency)
    {
        if (_selection is null) return;
        graph.UsePass(new(_prefix + "stream-select"), "pbr-stream-hierarchy", dependency,
            static (in d, p) => d.View.WriteStreamWork(p),
            context => _selection.Select(context.CommandEncoder, 7), RenderGraphPassKind.Compute);
    }

    private void BuildStreamFeedback(ref RenderGraphBuildContext graph, Dependencies dependency)
    {
        if (_selection is null) return;
        graph.UsePass(new(_prefix + "stream-copy"), "pbr-stream-feedback-copy", dependency,
            static (in d, p) => {
                p.Read(d.View._streamFeedbackKey, RenderGraphBufferUsage.CopySource)
                    .Read(d.View._streamArgsKey, RenderGraphBufferUsage.CopySource);
                // Stable topology: only the available ring slot is actually copied.
                for (var i = 0; i < 3; i++)
                    p.Write(new(d.View._prefix + "stream-readback/" + i), RenderGraphBufferUsage.CopyDestination);
            }, context => _selection.CopyFeedback(context.CommandEncoder), RenderGraphPassKind.Compute);
    }

    private void DrawStreamGeometry(WgpuHandle<WGPURenderPassEncoder> pass, bool shadow, uint layer)
    {
        var selection = _selection!;
        Wgpu.SetBindGroup(pass, 0, _rasterGroup.GetWgpu<WGPUBindGroup>());
        Wgpu.SetBindGroup(pass, 1, _owner.Scene.Group.GetWgpu<WGPUBindGroup>());
        Wgpu.SetBindGroup(pass, 2, selection.RasterGroup(shadow ? (int)layer : 7).GetWgpu<WGPUBindGroup>());
        Wgpu.SetRenderPipeline(pass, (shadow ? _owner.Pipelines.StreamShadow : _owner.Pipelines.StreamRaster).GetWgpu<WGPURenderPipeline>());
        Wgpu.DrawIndirect(pass, selection.Arguments.GetWgpu<WGPUBuffer>(), 0);
        Wgpu.SetRenderPipeline(pass, (shadow ? _owner.Pipelines.StreamShadowDouble : _owner.Pipelines.StreamRasterDouble).GetWgpu<WGPURenderPipeline>());
        Wgpu.DrawIndirect(pass, selection.Arguments.GetWgpu<WGPUBuffer>(), 16);
    }
}
