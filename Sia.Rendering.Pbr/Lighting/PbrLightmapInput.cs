using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Sia.Engine.Mesh;
using Sia.Math;

namespace Sia.Engine.Rendering.Pbr;

/// <summary>A static opaque receiver; StaticInstance excludes dynamic slots but includes other static slots.</summary>
public readonly record struct PbrLightmapReceiver(int SourceInstance, int StaticInstance, int X, int Y, int Resolution,
    float4 ScaleBias);

/// <summary>A padded atlas rectangle owned by one canonical static receiver.</summary>
public readonly record struct PbrLightmapChart(int StaticInstance, int X, int Y, int Width, int Height);

[StructLayout(LayoutKind.Explicit, Size = 48)]
public readonly record struct PbrLightmapTexel(
    [field: FieldOffset(0)] float3 Position,
    [field: FieldOffset(16)] float3 Normal,
    [field: FieldOffset(32)] int StaticInstance,
    [field: FieldOffset(36)] int Chart,
    [field: FieldOffset(40)] int Triangle)
{
    public const int Stride = 48;
    [FieldOffset(44)] private readonly int _covered = Triangle >= 0 ? 1 : 0;
    public bool Covered => _covered != 0;
}

/// <summary>
/// CPU surface inputs for an offline irradiance bake. Scene contains newly cooked receiver
/// coordinates. FilterSources copies only within an individual padded chart; holes remain -1.
/// SurfaceIdentity covers this derived layout, not lights/environment/integration settings.
/// </summary>
public sealed partial class PbrLightmapInput
{
    public PbrSceneAsset Scene { get; }
    public int Resolution { get; }
    public ReadOnlyMemory<PbrLightmapReceiver> Receivers { get; }
    public ReadOnlyMemory<PbrLightmapTexel> Texels { get; }
    public ReadOnlyMemory<int> FilterSources { get; }
    public ReadOnlyMemory<byte> SurfaceIdentity { get; }
    public int CoveredTexelCount { get; }
    public ReadOnlyMemory<PbrLightmapChart> Charts { get; }

    private PbrLightmapInput(PbrSceneAsset scene, int resolution, PbrLightmapReceiver[] receivers,
        PbrLightmapTexel[] texels, int[] filterSources, byte[] identity, int covered, PbrLightmapChart[]? charts = null)
    {
        Scene = scene;
        Resolution = resolution;
        Receivers = receivers;
        Texels = texels;
        FilterSources = filterSources;
        SurfaceIdentity = identity;
        CoveredTexelCount = covered;
        Charts = charts ?? [];
    }

    /// <summary>
    /// Generates mesh-local charts once per receiver geometry, then gives each static opaque
    /// instance a distinct square tile. Enforces the dense surface-buffer budget before cooking.
    /// This initial allocation uses one caller-selected resolution per receiver.
    /// </summary>
    public static PbrLightmapInput Create(PbrSceneAsset scene, int receiverResolution = 64, int padding = 2,
        int maximumAtlasResolution = 4096, ulong maximumBytes = 128ul * 1024 * 1024,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scene);
        if (receiverResolution is < 8 or > 8192) throw new ArgumentOutOfRangeException(nameof(receiverResolution));
        if (padding < 1 || padding > (receiverResolution - 2) / 2) throw new ArgumentOutOfRangeException(nameof(padding));
        if (maximumAtlasResolution is < 8 or > 8192) throw new ArgumentOutOfRangeException(nameof(maximumAtlasResolution));
        cancellationToken.ThrowIfCancellationRequested();
        var receivers = new List<PbrLightmapReceiver>();
        var staticIndex = 0;
        for (var source = 0; source < scene.Instances.Length; source++) {
            if ((source & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
            var instance = scene.Instances.Span[source];
            if (instance.Dynamic) continue;
            if (!scene.Materials.Span[instance.Material].AlphaBlend)
                receivers.Add(new(source, staticIndex, 0, 0, receiverResolution, default));
            staticIndex++;
        }
        if (receivers.Count == 0) throw new InvalidOperationException("Surface baking requires at least one static opaque receiver.");
        var tiles = (int)System.Math.Ceiling(System.Math.Sqrt(receivers.Count));
        var resolution = (long)tiles * receiverResolution;
        var texelCount = (ulong)(resolution * resolution);
        if (resolution > maximumAtlasResolution || texelCount * (PbrLightmapTexel.Stride + sizeof(int)) > maximumBytes)
            throw new InvalidOperationException("Lightmap surface inputs exceed the configured atlas dimension or byte budget.");
        var atlasResolution = (int)resolution;
        for (var i = 0; i < receivers.Count; i++) {
            var x = i % tiles * receiverResolution;
            var y = i / tiles * receiverResolution;
            var scale = (float)receiverResolution / atlasResolution;
            receivers[i] = receivers[i] with { X = x, Y = y, ScaleBias = new(scale, scale,
                (float)x / atlasResolution, (float)y / atlasResolution) };
        }
        var geometry = scene.Geometry.ToArray();
        var layouts = new Dictionary<int, MeshLightmapLayout>();
        foreach (var receiver in receivers) {
            cancellationToken.ThrowIfCancellationRequested();
            var g = scene.Instances.Span[receiver.SourceInstance].Geometry;
            if (layouts.ContainsKey(g)) continue;
            var finest = geometry[g].ExtractFinest().Build.Tree;
            var layout = MeshLightmap.Generate(new(finest.Vertices.ToArray(), finest.Indices.ToArray(), finest.Bounds),
                receiverResolution, padding, cancellationToken);
            layouts.Add(g, layout);
            geometry[g] = MeshPatchAsset.Cook(layout.Mesh, geometry[g].Settings, cancellationToken);
        }
        var mapped = PbrSceneAsset.Create(geometry, scene.Materials.Span, scene.Instances.Span, scene.Attribution);
        var texels = new PbrLightmapTexel[(int)texelCount];
        Array.Fill(texels, new(default, default, -1, -1, -1));
        var sources = new int[texels.Length];
        Array.Fill(sources, -1);
        var covered = 0;
        foreach (var receiver in receivers) {
            cancellationToken.ThrowIfCancellationRequested();
            var instance = mapped.Instances.Span[receiver.SourceInstance];
            var layout = layouts[instance.Geometry];
            covered += Rasterize(layout, receiver, instance.Transform, atlasResolution, texels, sources, cancellationToken);
            Dilate(layout, receiver, atlasResolution, texels, sources, cancellationToken);
        }
        return new(mapped, atlasResolution, receivers.ToArray(), texels, sources,
            Identity(mapped, receivers, atlasResolution, receiverResolution, padding, layouts), covered,
            AtlasCharts(mapped, receivers, layouts));
    }

    internal static PbrLightmapChart[] AtlasCharts(PbrSceneAsset scene, IReadOnlyList<PbrLightmapReceiver> receivers,
        Dictionary<int, MeshLightmapLayout> layouts)
    {
        long count = 0;
        foreach (var receiver in receivers) count += layouts[scene.Instances.Span[receiver.SourceInstance].Geometry].Charts.Length;
        if (count > 1_000_000) throw new InvalidOperationException("Too many lightmap charts.");
        var charts = new List<PbrLightmapChart>((int)count);
        foreach (var receiver in receivers) {
            var layout = layouts[scene.Instances.Span[receiver.SourceInstance].Geometry];
            foreach (var chart in layout.Charts.Span)
                charts.Add(new(receiver.StaticInstance, receiver.X + chart.X, receiver.Y + chart.Y, chart.Width, chart.Height));
        }
        return charts.ToArray();
    }

    private static int Rasterize(MeshLightmapLayout layout, PbrLightmapReceiver receiver, float4x4 transform,
        int atlasResolution, PbrLightmapTexel[] texels, int[] sources, CancellationToken cancellationToken)
    {
        var normalMatrix = math.transpose(math.inverse(transform));
        var mesh = layout.Mesh;
        var counts = new int[layout.Charts.Length];
        bool[]? emptyCharts = null;
        for (var pass = 0; pass < 2; pass++) {
            if (pass == 1) {
                emptyCharts = counts.Select(count => count == 0).ToArray();
                if (!emptyCharts.Any(empty => empty)) break;
            }
            for (var t = 0; t < mesh.Indices.Length / 3; t++) {
                if ((t & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                var chart = layout.TriangleCharts.Span[t];
                if (pass == 1 && !emptyCharts![chart]) continue;
                var a = mesh.Vertices[mesh.Indices[t * 3]];
                var b = mesh.Vertices[mesh.Indices[t * 3 + 1]];
                var c = mesh.Vertices[mesh.Indices[t * 3 + 2]];
                var pa = math.mul(transform, new float4(a.Position, 1)).xyz;
                var pb = math.mul(transform, new float4(b.Position, 1)).xyz;
                var pc = math.mul(transform, new float4(c.Position, 1)).xyz;
                var na = math.mul(normalMatrix, new float4(a.Normal, 0)).xyz;
                var nb = math.mul(normalMatrix, new float4(b.Normal, 0)).xyz;
                var nc = math.mul(normalMatrix, new float4(c.Normal, 0)).xyz;
                if (!math.all(math.isfinite(pa)) || !math.all(math.isfinite(pb)) || !math.all(math.isfinite(pc))
                    || !math.all(math.isfinite(na)) || !math.all(math.isfinite(nb)) || !math.all(math.isfinite(nc)))
                    throw new InvalidOperationException("World-space lightmap positions or normals are not finite.");
                var uvA = new double2(a.LightmapUV.x, a.LightmapUV.y) * layout.Resolution;
                var uvB = new double2(b.LightmapUV.x, b.LightmapUV.y) * layout.Resolution;
                var uvC = new double2(c.LightmapUV.x, c.LightmapUV.y) * layout.Resolution;
                var area = Cross(uvB - uvA, uvC - uvA);
                if (!double.IsFinite(area) || area <= 0) throw new InvalidOperationException($"Generated lightmap coordinates collapsed or flipped at triangle {t}, chart {chart}.");
                var min = math.min(uvA, math.min(uvB, uvC));
                var max = math.max(uvA, math.max(uvB, uvC));
                var x0 = System.Math.Clamp((int)(pass == 0 ? System.Math.Ceiling(min.x - .5 - 1e-5) : System.Math.Floor(min.x)), 0, layout.Resolution - 1);
                var x1 = System.Math.Clamp((int)System.Math.Floor(max.x - (pass == 0 ? .5 - 1e-5 : 0)), 0, layout.Resolution - 1);
                var y0 = System.Math.Clamp((int)(pass == 0 ? System.Math.Ceiling(min.y - .5 - 1e-5) : System.Math.Floor(min.y)), 0, layout.Resolution - 1);
                var y1 = System.Math.Clamp((int)System.Math.Floor(max.y - (pass == 0 ? .5 - 1e-5 : 0)), 0, layout.Resolution - 1);
                for (var y = y0; y <= y1; y++) {
                    cancellationToken.ThrowIfCancellationRequested();
                    for (var x = x0; x <= x1; x++) {
                        var p = new double2(x + .5, y + .5);
                        if (pass == 1) {
                            if (!IntersectsTexel(uvA, uvB, uvC, p)) continue;
                            // A thin chart can miss every center at any resolution. The
                            // footprint is covered, but the ray origin must remain on the
                            // real triangle rather than extrapolating the texel center.
                            p = ClosestSurfacePoint(uvA, uvB, uvC, p);
                        }
                        var wa = Cross(uvB - p, uvC - p) / area;
                        var wb = Cross(uvC - p, uvA - p) / area;
                        var wc = 1 - wa - wb;
                        if (wa < -1e-6 || wb < -1e-6 || wc < -1e-6) continue;
                        // Float UV transport can round an exact edge center just outside its
                        // triangle. Clamp only that numerical fringe back onto the surface.
                        wa = System.Math.Max(0, wa);
                        wb = System.Math.Max(0, wb);
                        wc = System.Math.Max(0, wc);
                        var sum = wa + wb + wc;
                        wa /= sum;
                        wb /= sum;
                        wc /= sum;
                        var position = pa * (float)wa + pb * (float)wb + pc * (float)wc;
                        var normal = na * (float)wa + nb * (float)wb + nc * (float)wc;
                        var length = math.length(normal);
                        if (length < 1e-12f) { normal = math.cross(pb - pa, pc - pa); length = math.length(normal); }
                        if (!float.IsFinite(length) || length <= 0 || !math.all(math.isfinite(position)))
                            throw new InvalidOperationException("Lightmap sample has an invalid surface frame.");
                        normal /= length;
                        var index = (receiver.Y + y) * atlasResolution + receiver.X + x;
                        if (texels[index].Covered) {
                            // Shared-edge centers are covered by both incident triangles. The chart
                            // generator has already rejected positive-area projected overlap.
                            if (texels[index].StaticInstance != receiver.StaticInstance || texels[index].Chart != chart
                                || (pass == 0 && math.length(texels[index].Position - position) > 1e-4f * System.Math.Max(1, math.length(position))))
                                throw new InvalidOperationException("Lightmap rasterization found incompatible overlapping samples.");
                            continue;
                        }
                        texels[index] = new(position, normal, receiver.StaticInstance, chart, t);
                        sources[index] = index;
                        counts[chart]++;
                    }
                }
            }
        }
        var emptyChart = Array.IndexOf(counts, 0);
        if (emptyChart >= 0)
            throw new InvalidOperationException($"Lightmap chart {emptyChart} covers no texel center at resolution {layout.Resolution}; increase the receiver resolution.");
        return counts.Sum();
    }

    private static void Dilate(MeshLightmapLayout layout, PbrLightmapReceiver receiver, int atlasResolution,
        PbrLightmapTexel[] texels, int[] sources, CancellationToken cancellationToken)
    {
        var queue = new Queue<int>();
        for (var chart = 0; chart < layout.Charts.Length; chart++) {
            cancellationToken.ThrowIfCancellationRequested();
            var rect = layout.Charts.Span[chart];
            var x0 = receiver.X + rect.X;
            var y0 = receiver.Y + rect.Y;
            var x1 = x0 + rect.Width;
            var y1 = y0 + rect.Height;
            queue.Clear();
            for (var y = y0; y < y1; y++) {
                cancellationToken.ThrowIfCancellationRequested();
                for (var x = x0; x < x1; x++) {
                    var index = y * atlasResolution + x;
                    if (texels[index].Covered && texels[index].StaticInstance == receiver.StaticInstance && texels[index].Chart == chart)
                        queue.Enqueue(index);
                }
            }
            // Chebyshev-radius dilation covers diagonal filtering footprints. Filtering
            // addresses are values only; invalid texels do not become ray origins.
            for (var step = 0; step < layout.Padding; step++) {
                cancellationToken.ThrowIfCancellationRequested();
                var count = queue.Count;
                for (var i = 0; i < count; i++) {
                    if ((i & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
                    var index = queue.Dequeue();
                    var px = index % atlasResolution;
                    var py = index / atlasResolution;
                    for (var dy = -1; dy <= 1; dy++)
                        for (var dx = -1; dx <= 1; dx++) {
                            var x = px + dx;
                            var y = py + dy;
                            if (x < x0 || x >= x1 || y < y0 || y >= y1) continue;
                            var other = y * atlasResolution + x;
                            if (sources[other] >= 0) continue;
                            sources[other] = sources[index];
                            queue.Enqueue(other);
                        }
                }
            }
        }
    }

    private static double Cross(double2 a, double2 b) => a.x * b.y - a.y * b.x;

    private static bool IntersectsTexel(double2 a, double2 b, double2 c, double2 center)
    {
        // Bounds supplied by the caller cover the two box axes. Test the three
        // triangle edge normals to finish the triangle/axis-aligned-box SAT.
        return Edge(a, b) && Edge(b, c) && Edge(c, a);
        bool Edge(double2 from, double2 to) {
            var delta = to - from;
            var pa = Cross(delta, a - center);
            var pb = Cross(delta, b - center);
            var pc = Cross(delta, c - center);
            var radius = .5 * (System.Math.Abs(delta.x) + System.Math.Abs(delta.y));
            return System.Math.Min(pa, System.Math.Min(pb, pc)) <= radius
                && System.Math.Max(pa, System.Math.Max(pb, pc)) >= -radius;
        }
    }

    private static double2 ClosestSurfacePoint(double2 a, double2 b, double2 c, double2 center)
    {
        if (Cross(b - a, center - a) >= 0 && Cross(c - b, center - b) >= 0
            && Cross(a - c, center - c) >= 0) return center;
        var result = a;
        var distance = double.PositiveInfinity;
        Edge(a, b); Edge(b, c); Edge(c, a);
        return result;
        void Edge(double2 from, double2 to) {
            var delta = to - from;
            var lengthSquared = delta.x * delta.x + delta.y * delta.y;
            var fraction = System.Math.Clamp(((center.x - from.x) * delta.x + (center.y - from.y) * delta.y) / lengthSquared, 0, 1);
            var point = from + fraction * delta;
            var d = point - center;
            var candidate = d.x * d.x + d.y * d.y;
            if (candidate < distance) { distance = candidate; result = point; }
        }
    }

    private static byte[] Identity(PbrSceneAsset scene, List<PbrLightmapReceiver> receivers, int resolution,
        int receiverResolution, int padding, Dictionary<int, MeshLightmapLayout> layouts)
    {
        var hash = new SceneIdentityHash();
        hash.AppendData("SIALMSURFACE2"u8);
        hash.AppendData(PbrSceneTransport.StaticIdentity(scene));
        Span<byte> values = stackalloc byte[24];
        void Append(int a, int b, int c, int d) {
            Span<byte> record = stackalloc byte[16];
            BinaryPrimitives.WriteInt32LittleEndian(record, a);
            BinaryPrimitives.WriteInt32LittleEndian(record[4..], b);
            BinaryPrimitives.WriteInt32LittleEndian(record[8..], c);
            BinaryPrimitives.WriteInt32LittleEndian(record[12..], d);
            hash.AppendData(record);
        }
        Append(resolution, receiverResolution, padding, receivers.Count);
        var seen = new HashSet<int>();
        foreach (var receiver in receivers) {
            // Authored slot offsets intentionally do not participate in the static identity.
            Append(receiver.StaticInstance, receiver.X, receiver.Y, receiver.Resolution);
            var g = scene.Instances.Span[receiver.SourceInstance].Geometry;
            if (!seen.Add(g)) continue;
            hash.AppendData(Convert.FromHexString(scene.Geometry.Span[g].SourceHash));
            var settings = scene.Geometry.Span[g].Settings;
            BinaryPrimitives.WriteInt32LittleEndian(values, settings.MaxLeafTriangles);
            BinaryPrimitives.WriteInt32LittleEndian(values[4..], settings.MaxChildren);
            BinaryPrimitives.WriteSingleLittleEndian(values[8..], settings.ParentTriangleRatio);
            BinaryPrimitives.WriteSingleLittleEndian(values[12..], settings.NormalWeight);
            BinaryPrimitives.WriteSingleLittleEndian(values[16..], settings.UVWeight);
            hash.AppendData(values[..20]);
            Append(layouts[g].Charts.Length, layouts[g].TriangleCharts.Length, 0, 0);
            foreach (var rect in layouts[g].Charts.Span) Append(rect.X, rect.Y, rect.Width, rect.Height);
        }
        return hash.Finish();
    }
}
