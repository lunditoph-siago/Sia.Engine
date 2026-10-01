using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using Sia;
using Sia.Engine.Camera;
using Sia.Engine.Lighting;
using Sia.Graphics.Reactive;
using Sia.Math;
using Sia.RenderGraph;
using Sia.WebGPU;
using CameraComponent = Sia.Engine.Camera.Camera;

namespace Sia.Engine.Rendering.Pbr;

internal sealed unsafe partial class PbrView
{
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Prepare(in RenderFrameContext frame)
    {
        _frame = frame;
        _selection?.Prepare(_owner.Scene.Streaming!);
        if (_materialRevision != _owner.ResourceRevision) {
            _sceneTextures.Clear();
            foreach (var texture in _owner.Materials.Textures)
                _sceneTextures.Add((new(_prefix + "scene-texture/" + _sceneTextures.Count), texture));
            _sceneTextures.Add((new(_prefix + "scene-texture/" + _sceneTextures.Count), _owner.Environment.Cube));
            _sceneTextures.Add((new(_prefix + "scene-texture/" + _sceneTextures.Count), _owner.Environment.Lut));
            _materialRevision = _owner.ResourceRevision;
        }
        var viewport = frame.Viewport ?? frame.Frame.MainWorld.AcquireAddon<Viewport>().Value;
        if (!float.IsFinite(viewport.Width) || !float.IsFinite(viewport.Height) || viewport.Width < 1 || viewport.Height < 1)
            throw new ArgumentOutOfRangeException(nameof(frame), "Frame viewport dimensions must be finite and positive.");
        Resize((uint)viewport.Width, (uint)viewport.Height);
        var camera = frame.Camera.Get<CameraMatrices>();
        var parameters = frame.Camera.Get<CameraComponent>();
        if (!float.IsFinite(parameters.Near) || !float.IsFinite(parameters.Far) || parameters.Near <= 0 || parameters.Far <= parameters.Near)
            throw new ArgumentException("Camera requires finite 0 < near < far.");
        _data = new() {
            ViewProjection = camera.ViewProj, View = camera.View, InverseProjection = math.inverse(camera.Proj), InverseViewProjection = camera.InvViewProj,
            Eye = new(camera.WorldPosition, 1), Size = new(_width, _height, (uint)_owner.Materials.Groups.Length, (uint)_owner.DebugMode),
            Geometry = new(_owner.Scene.VertexCount, _owner.Scene.TriangleOffset,
                _owner.Scene.Streaming?.Hierarchy is { } hierarchy ? hierarchy.SingleCapacity + hierarchy.DoubleCapacity : _owner.Scene.OpaqueTriangles,
                _owner.Scene.TriangleCount),
            Grid = new(16, 9, 24, k_LightsPerCell), Counts = new(0, 0, uint.MaxValue, k_MaximumLights * 4)
        };
        var scale = 24 / MathF.Log(parameters.Far / parameters.Near);
        _data.Depth = new(scale, MathF.Log(parameters.Near) * scale, 1, 0);
        _directional.Clear();
        _casters.Clear();
        Array.Clear(_shadowActive);
        _spotCount = 0;
        frame.Frame.MainWorld.Query(s_Casters, _collectCaster);
        frame.Frame.MainWorld.Query(s_Directionals, _collectDirectional);
        var directionalData = MemoryMarshal.CreateSpan(ref _data.Direction0, 8);
        var shadowConfig = frame.Frame.MainWorld.AcquireAddon<ShadowAtlasConfig>();
        if (shadowConfig.CascadeCount is < 0 or > 3 || shadowConfig.MaxShadowedSpotLights is < 0 or > 4)
            throw new ArgumentException("Resident PBR supports 0..3 cascades and 0..4 shadowed spot lights.");
        for (var i = 0; i < _directional.Count; i++) {
            var e = _directional[i];
            var light = e.Get<LightColor>();
            var direction = -math.normalize(e.Get<GlobalTransform>().Affine.RotationScale.c2);
            directionalData[i * 2] = new(direction, 0);
            directionalData[(i * 2) + 1] = new(light.Color * light.Intensity, 0);
            if (_data.Counts.z != uint.MaxValue || !_casters.Contains(e) || shadowConfig.CascadeCount == 0) continue;
            _data.Counts.z = (uint)i;
            _data.Shadow.x = (uint)shadowConfig.CascadeCount;
            var far = System.Math.Min(parameters.Far, shadowConfig.ShadowDistance);
            if (far <= parameters.Near) throw new ArgumentException("Shadow distance must exceed camera near plane.");
            var splits = CascadeSplitting.ComputeSplitDistances(parameters.Near, far, shadowConfig.CascadeCount, shadowConfig.CascadeSplitLambda);
            _data.Splits = new(splits.Length > 1 ? splits[1] : far, splits.Length > 2 ? splits[2] : far, far, 0);
            var transform = frame.Camera.Get<GlobalTransform>().Affine;
            for (var layer = 0; layer < shadowConfig.CascadeCount; layer++) {
                var matrix = CascadeSplitting.ComputeCascadeViewProj(transform, parameters.VerticalFovRadians, (float)_width / _height,
                    splits[layer], splits[layer + 1], direction, shadowConfig.CascadeShadowPullback, _owner.Scene.Bounds, _owner.Settings.ShadowResolution);
                var cached = _shadowMatrices[layer];
                if (_shadowValid[layer] && cached.c0.Equals(matrix.c0) && cached.c1.Equals(matrix.c1)
                    && cached.c2.Equals(matrix.c2) && cached.c3.z == matrix.c3.z
                    && CascadeSplitting.ContainsReceiverFrustum(cached, transform, parameters.VerticalFovRadians,
                        (float)_width / _height, splits[layer], splits[layer + 1], _owner.Settings.ShadowResolution))
                    matrix = cached;
                SetShadow(layer, matrix);
            }
        }
        _data.Counts.y = (uint)_directional.Count;
        if (_owner.Probes is { Dynamic: true } probes) {
            var sky = frame.Frame.MainWorld.AcquireAddon<EnvironmentLighting>().Sky;
            probes.Prepare(sky, _directional.Count == 0 ? sky.SunDirection : -_data.Direction0.xyz,
                _directional.Count == 0 ? float3.zero : _data.Radiance0.xyz,
                _owner.Settings.ProbeUpdates, _owner.Settings.ProbeSamples);
        }
        frame.Frame.MainWorld.Query(s_Points, _collectPoint);
        frame.Frame.MainWorld.Query(s_Spots, _collectSpot);
        Wgpu.WriteBuffer<PbrFrame>(_gpu.Queue, _uniform.GetWgpu<WGPUBuffer>(), 0, [_data]);
        Wgpu.WriteBuffer<float4>(_gpu.Queue, _lightData.GetWgpu<WGPUBuffer>(), 0, _sceneData);
        var culler = new FrustumCuller(camera.Frustum);
        _opaque.Clear();
        _transparent.Clear();
        if (_owner.Scene.Streaming is { } streaming) {
            if (_selection is not null)
                _selection.Configure(7, camera.ViewProj, _width, _height, _owner.Settings.TargetPixelError,
                    (uint)_owner.Settings.Streaming.MaximumSelectionNodesPerView);
            else {
                streaming.Select(camera.ViewProj, _width, _height, _owner.Settings.TargetPixelError, culler, _opaque);
                CompactStreamDraws(_opaque, UseForward);
            }
        }
        else foreach (var draw in _owner.Scene.Opaque)
            if (culler.Intersects(draw.Bounds)) {
                if (UseForward)
                    AddRange(_opaque, SelectGeometry(draw, camera.ViewProj, _width, _height, _owner.Settings.TargetPixelError), true);
                else AddRange(_opaque, SelectGeometry(draw, camera.ViewProj, _width, _height, _owner.Settings.TargetPixelError));
            }
        foreach (var draw in _owner.Scene.Transparent)
            if (culler.Intersects(draw.Bounds)) _transparent.Add(draw);

        var opaqueTriangles = _selection?.VisibleTriangles ?? 0;
        if (_selection is null) foreach (var draw in _opaque) opaqueTriangles = checked(opaqueTriangles + draw.Count);
        var transparentTriangles = 0u;
        _needsSnapshot = false;
        foreach (var draw in _transparent) {
            transparentTriangles = checked(transparentTriangles + draw.Count);
            _needsSnapshot |= _owner.Materials.Transmission[draw.Material];
        }
        VisibleTriangles = checked(opaqueTriangles + transparentTriangles);
        if (_owner.Materials.IsStreaming) {
            Array.Clear(_materialPixels);
            void TextureDemand(PbrGpuScene.Draw draw)
            {
                var pixels = ProjectedGeometryError.ProjectError(draw.Bounds, math.length(draw.Bounds.Max - draw.Bounds.Min), camera.ViewProj, _width, _height);
                _materialPixels[draw.Material] = System.Math.Max(_materialPixels[draw.Material], float.IsFinite(pixels) ? pixels : System.Math.Max(_width, _height));
            }
            foreach (var draw in _opaque) TextureDemand(draw);
            if (_selection is not null) {
                var stream = _owner.Scene.Streaming!;
                for (var i = 0; i < stream.Source.Instances.Length; i++) {
                    var bounds = stream.InstanceBounds[i];
                    if (!culler.Intersects(bounds)) continue;
                    var material = stream.Source.Instances.Span[i].MaterialIndex;
                    var pixels = ProjectedGeometryError.ProjectError(bounds, math.length(bounds.Max - bounds.Min), camera.ViewProj, _width, _height);
                    _materialPixels[material] = System.Math.Max(_materialPixels[material], float.IsFinite(pixels) ? pixels : System.Math.Max(_width, _height));
                }
            }
            foreach (var draw in _transparent) TextureDemand(draw);
            for (var material = 0; material < _materialPixels.Length; material++)
                if (_materialPixels[material] > 0) _owner.Materials.Demand(material, _materialPixels[material]);
        }
        _transparentEye = camera.WorldPosition;
        _transparent.Sort(_sortTransparent);
    }

    private int CompareTransparent(PbrGpuScene.Draw a, PbrGpuScene.Draw b)
    {
        var order = math.lengthsq(b.Center - _transparentEye).CompareTo(math.lengthsq(a.Center - _transparentEye));
        return order != 0 ? order : a.First.CompareTo(b.First);
    }
}
