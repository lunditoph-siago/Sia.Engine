using Sia.Engine.Camera;
using Sia.Engine.Lighting;
using Sia.Engine.Mesh;
using Sia;
using Sia.Engine;
using Sia.Math;

namespace Sia.Engine.Rendering.Pbr;

public sealed class PbrRenderCache : SnapshotExtractSystem<PbrRenderInstance>, IAddon
{
    private const int BatchSize = 128;

    private readonly List<MeshHandle> _meshHandles = [];
    private readonly List<Aabb> _worldBounds = [];
    private readonly List<int> _visibleIndices = [];

    void IAddon.OnInitialize(World world) => Initialize(world);

    protected override IEntityMatcher ExtractMatcher =>
        Matchers.Of<global::Sia.Engine.Mesh.Mesh, PbrMaterial, MeshRenderer, GlobalTransform, WorldBounds>();

    protected override PbrRenderInstance Extract(Entity entity)
    {
        _meshHandles.Add(entity.Get<global::Sia.Engine.Mesh.Mesh>().Handle);
        _worldBounds.Add(entity.Get<WorldBounds>().World);

        var worldTransform = entity.Get<GlobalTransform>().Affine;
        float4x4 worldMatrix = worldTransform;
        var material = entity.Get<PbrMaterial>();
        return new PbrRenderInstance(
            worldMatrix,
            ComputeNormalMatrix(worldTransform),
            new float4(material.BaseColor, 1.0f),
            new float4(material.Metallic, material.Roughness, 0.0f, 0.0f),
            new float4(material.EmissiveColor, material.EmissiveStrength));
    }

    public void Refresh()
    {
        _meshHandles.Clear();
        _worldBounds.Clear();
        RunExtract();
    }

    public IReadOnlyList<MeshHandle> MeshHandles => _meshHandles;

    public IReadOnlyList<int> Cull(Frustum frustum)
    {
        _visibleIndices.Clear();
        var count = _worldBounds.Count;

        for (var batchStart = 0; batchStart < count; batchStart += BatchSize) {
            var batchEnd = System.Math.Min(batchStart + BatchSize, count);
            var batchAabb = _worldBounds[batchStart];
            for (var i = batchStart + 1; i < batchEnd; i++) {
                batchAabb.Include(_worldBounds[i]);
            }
            if (!frustum.Intersects(batchAabb)) {
                continue;
            }

            for (var i = batchStart; i < batchEnd; i++) {
                if (frustum.Intersects(_worldBounds[i])) {
                    _visibleIndices.Add(i);
                }
            }
        }

        return _visibleIndices;
    }

    private static float4x4 ComputeNormalMatrix(in AffineTransform worldTransform)
    {
        var normal = math.transpose(math.inverse(worldTransform.RotationScale));
        return new float4x4(normal, float3.zero);
    }
}
