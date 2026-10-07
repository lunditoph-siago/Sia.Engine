#define_import_path pbr/bindings

#import pbr/frame_types
#import pbr/stream_work_types

// Hybrid conventional triangles address topology directly; virtual triangles address visible_work.
const CONVENTIONAL_TRIANGLE_BIT: u32 = 0x80000000u;

fn valid_triangle_id(id: u32) -> bool {
    if (id == 0u) { return false; }
#if SHADING_WORK
    let ordinal = id - 1u;
    if ((ordinal & CONVENTIONAL_TRIANGLE_BIT) != 0u) {
        return (ordinal & ~CONVENTIONAL_TRIANGLE_BIT) < frame.geometry.w;
    }
#endif
    return id <= frame.geometry.z;
}

struct Material {
    color: vec4<f32>,
    emission: vec4<f32>,
    factors: vec4<f32>,
    layers: vec4<u32>,
    indices: vec4<u32>,
    transport: vec4<f32>,
}

@group(0) @binding(0) var<uniform> frame: Frame;
#if RASTER_FRAME
@group(0) @binding(1) var<uniform> scene_data: array<vec4<f32>, 28>;
#else
@group(0) @binding(1) var<storage, read> scene_data: array<vec4<f32>>;
#endif
#if WRITABLE_CLUSTERS
@group(0) @binding(2) var<storage, read_write> clusters: array<u32>;
#else
@group(0) @binding(2) var<storage, read> clusters: array<u32>;
#endif
@group(0) @binding(3) var shadow_atlas: texture_depth_2d_array;
@group(0) @binding(4) var environment: texture_cube<f32>;
@group(0) @binding(5) var brdf_lut: texture_2d<f32>;
@group(0) @binding(6) var environment_sampler: sampler;
@group(0) @binding(7) var brdf_sampler: sampler;
@group(0) @binding(11) var depth_sampler: sampler_comparison;
@group(0) @binding(8) var<uniform> sh: array<vec4<f32>, 9>;
#if LIGHTMAPS
@group(0) @binding(12) var lightmap_atlas: texture_2d_array<f32>;
#endif
#if SCENE_GI
@group(0) @binding(9) var probe_volume: texture_3d<f32>;
@group(0) @binding(10) var<uniform> probe_header: array<vec4<f32>,3>;
#endif
#if LIGHTMAP_SCENE_GI
@group(0) @binding(13) var probe_difference: texture_3d<f32>;
#endif
#if COMPACT_VERTICES
@group(1) @binding(0) var<storage, read> vertices: array<vec4<u32>>;
#else
@group(1) @binding(0) var<storage, read> vertices: array<vec4<f32>>;
#endif

fn geometry_record(index: u32) -> vec4<f32> {
#if COMPACT_VERTICES
    return bitcast<vec4<f32>>(vertices[index]);
#else
    return vertices[index];
#endif
}
@group(1) @binding(1) var<storage, read> topology: array<u32>;
#if HAS_INSTANCES
#import pbr/instance_types
@group(1) @binding(2) var<storage, read> instances: array<Instance>;
#endif
#if SHADING_WORK
@group(3) @binding(6) var<storage, read> visible_work: array<vec4<u32>>;

fn stream_triangle(ordinal: u32) -> vec2<u32> {
    let record = visible_work[ordinal / STREAM_WORK_BLOCK_TRIANGLES];
    return vec2<u32>(record.x + ordinal % STREAM_WORK_BLOCK_TRIANGLES, record.y);
}
#endif
@group(2) @binding(0) var<storage, read> materials: array<Material>;
@group(2) @binding(1) var<uniform> batch: vec4<u32>;
@group(2) @binding(2) var albedo: texture_2d_array<f32>;
@group(2) @binding(3) var albedo_sampler: sampler;
@group(2) @binding(4) var normal_map: texture_2d_array<f32>;
@group(2) @binding(5) var normal_sampler: sampler;
@group(2) @binding(6) var mr_map: texture_2d_array<f32>;
@group(2) @binding(7) var mr_sampler: sampler;
@group(2) @binding(8) var ao_map: texture_2d_array<f32>;
@group(2) @binding(9) var ao_sampler: sampler;
@group(2) @binding(10) var emission_map: texture_2d_array<f32>;
@group(2) @binding(11) var emission_sampler: sampler;

fn unit(v: vec3<f32>) -> vec3<f32> {
    return v * inverseSqrt(max(dot(v, v), 1e-20));
}

fn shadow_matrix(layer: u32) -> mat4x4<f32> {
#if RASTER_FRAME
    let i = layer * 4u;
#else
    let i = #{SHADOW_MATRIX_BASE}u + layer * 4u;
#endif
    return mat4x4<f32>(scene_data[i], scene_data[i + 1u], scene_data[i + 2u], scene_data[i + 3u]);
}

fn vertex_index(triangle: u32, corner: u32) -> u32 {
#if SHADING_WORK
    if ((triangle & CONVENTIONAL_TRIANGLE_BIT) != 0u) {
        return topology[(triangle & ~CONVENTIONAL_TRIANGLE_BIT) * 3u + corner];
    }
    return topology[stream_triangle(triangle).x * 3u + corner];
#else
    return topology[triangle * 3u + corner];
#endif
}

struct Vertex {
    position: vec3<f32>,
    normal: vec3<f32>,
    uv: vec2<f32>,
    tangent: vec4<f32>,
    lightmap: vec3<f32>
}

#if COMPACT_VERTICES
fn vertex_owner(word: u32) -> u32 {
#if PACKED_LIGHTMAP_MARKERS
    return word & ((1u << #{LIGHTMAP_OWNER_BITS}u) - 1u);
#else
    return word & 0x7fffffffu;
#endif
}

fn unpack_direction(packed: u32) -> vec3<f32> {
    if (packed == 0x80008000u) { return vec3<f32>(0); }
    let xy = unpack2x16snorm(packed);
    var n = vec3<f32>(xy, 1.0 - abs(xy.x) - abs(xy.y));
    let fold = max(-n.z, 0.0);
    n.x += select(fold, -fold, n.x >= 0.0);
    n.y += select(fold, -fold, n.y >= 0.0);
    return unit(n);
}

fn compact_vertex(p: vec4<u32>, n: vec4<u32>, lightmap: vec3<f32>) -> Vertex {
    return Vertex(bitcast<vec3<f32>>(p.xyz), unpack_direction(p.w), bitcast<vec2<f32>>(n.xy),
        vec4<f32>(unpack_direction(n.z), select(1.0, -1.0, (n.w & 0x80000000u) != 0u)), lightmap);
}
#endif

fn lightmap_metadata_base() -> u32 {
#if COMPACT_VERTICES
#if PACKED_LIGHTMAP_MARKERS
    let base = frame.geometry.x * 2u + (frame.geometry.x + 3u) / 4u;
#else
    let base = frame.geometry.x * 2u + (frame.geometry.x + 1u) / 2u;
#endif
#if STREAM_LIGHTMAPS
    return base + 1u;
#else
    return base;
#endif
#else
    return frame.geometry.x * 4u;
#endif
}

fn vertex_lightmap(index: u32) -> vec3<f32> {
#if LIGHTMAPS
#if COMPACT_VERTICES
#if PACKED_LIGHTMAP_MARKERS
    let uv = vertices[frame.geometry.x * 2u + index / 4u][index % 4u];
    let marker = (vertices[frame.geometry.x + index].w & 0x7fffffffu) >> #{LIGHTMAP_OWNER_BITS}u;
    return vec3<f32>(unpack2x16unorm(uv), f32(marker));
#else
    let pair = vertices[frame.geometry.x * 2u + index / 2u];
    let component = (index % 2u) * 2u;
    return vec3<f32>(unpack2x16unorm(pair[component]), f32(pair[component + 1u]));
#endif
#else
    return geometry_record(frame.geometry.x * 3u + index).xyz;
#endif
#else
    return vec3<f32>(0);
#endif
}

#if STREAM_LIGHTMAPS
fn stream_lightmap(local: vec3<f32>, instance: u32) -> vec3<f32> {
    let prefix = lightmap_metadata_base() - 1u;
    let base = prefix + vertices[prefix].x + instance * 2u;
    let scale = geometry_record(base);
    let range = vertices[base + 1u];
    let uv = local.xy * scale.xy + scale.zw;
    if (range.y == 0u) { return vec3<f32>(uv, f32(range.z)); }
    let chart = u32(local.z);
    if (chart == 0u || chart > range.y) { return vec3<f32>(0); }
    let address = range.x + chart - 1u;
    let marker = vertices[prefix + range.w + address / 4u][address % 4u];
    return vec3<f32>(uv, f32(marker));
}
#endif

#if LOCAL_INSTANCES
fn resident_instance_index(triangle: u32, corner: u32) -> u32 {
#if COMPACT_VERTICES
    return vertex_owner(vertices[frame.geometry.x + vertex_index(triangle, corner)].w);
#else
    let tangent = geometry_record(frame.geometry.x * 2u + vertex_index(triangle, corner));
    return u32(abs(tangent.w)) - 1u;
#endif
}

fn resident_instance(triangle: u32, corner: u32) -> Instance {
    return instances[resident_instance_index(triangle, corner)];
}
#endif

#if SCENE_REFLECTIONS
fn receiver_instance_index(triangle: u32) -> u32 {
#if SHADING_WORK
    if ((triangle & CONVENTIONAL_TRIANGLE_BIT) == 0u) { return stream_triangle(triangle).y; }
#endif
    return resident_instance_index(triangle, 0u);
}
#endif

fn vertex_data(triangle: u32, corner: u32) -> Vertex {
    let index = vertex_index(triangle, corner);
#if COMPACT_VERTICES
    let vertex = compact_vertex(vertices[index], vertices[frame.geometry.x + index], vertex_lightmap(index));
#if SHADING_WORK
    if ((triangle & CONVENTIONAL_TRIANGLE_BIT) != 0u) {
#if LOCAL_INSTANCES
        let instance = resident_instance(triangle, corner);
        return Vertex((instance.transform * vec4<f32>(vertex.position, 1.0)).xyz,
            (instance.normal_transform * vec4<f32>(vertex.normal, 0.0)).xyz, vertex.uv,
            vec4<f32>((instance.transform * vec4<f32>(vertex.tangent.xyz, 0.0)).xyz, vertex.tangent.w), vertex.lightmap);
#else
        return vertex;
#endif
    }
    let instance_index = stream_triangle(triangle).y;
    let instance = instances[instance_index];
    return Vertex((instance.transform * vec4<f32>(vertex.position, 1.0)).xyz,
        (instance.normal_transform * vec4<f32>(vertex.normal, 0.0)).xyz, vertex.uv,
        vec4<f32>((instance.transform * vec4<f32>(vertex.tangent.xyz, 0.0)).xyz, vertex.tangent.w),
        stream_lightmap(vertex.lightmap, instance_index));
#else
#if LOCAL_INSTANCES
    let instance = resident_instance(triangle, corner);
    return Vertex((instance.transform * vec4<f32>(vertex.position, 1.0)).xyz,
        (instance.normal_transform * vec4<f32>(vertex.normal, 0.0)).xyz, vertex.uv,
        vec4<f32>((instance.transform * vec4<f32>(vertex.tangent.xyz, 0.0)).xyz, vertex.tangent.w), vertex.lightmap);
#else
    return vertex;
#endif
#endif
#else
    let p = geometry_record(index);
    let n = geometry_record(frame.geometry.x + index);
#if SHADING_WORK
    if ((triangle & CONVENTIONAL_TRIANGLE_BIT) != 0u) {
        let tangent = geometry_record(frame.geometry.x * 2u + index);
#if LOCAL_INSTANCES
        let instance = resident_instance(triangle, corner);
        return Vertex((instance.transform * vec4<f32>(p.xyz, 1.0)).xyz,
            (instance.normal_transform * vec4<f32>(p.w, n.xy, 0.0)).xyz, n.zw,
            vec4<f32>((instance.transform * vec4<f32>(tangent.xyz, 0.0)).xyz, sign(tangent.w)), vertex_lightmap(index));
#else
        return Vertex(p.xyz, vec3<f32>(p.w, n.xy), n.zw,
            vec4<f32>(tangent.xyz, sign(tangent.w)), vertex_lightmap(index));
#endif
    }
    let instance = instances[stream_triangle(triangle).y];
    let tangent = geometry_record(frame.geometry.x * 2u + index);
    return Vertex(
        (instance.transform * vec4<f32>(p.xyz, 1.0)).xyz,
        (instance.normal_transform * vec4<f32>(p.w, n.xy, 0.0)).xyz,
        n.zw,
        vec4<f32>((instance.transform * vec4<f32>(tangent.xyz, 0.0)).xyz, tangent.w), vertex_lightmap(index));
#else
#if LOCAL_INSTANCES
    let instance = resident_instance(triangle, corner);
    let tangent = geometry_record(frame.geometry.x * 2u + index);
    return Vertex(
        (instance.transform * vec4<f32>(p.xyz, 1.0)).xyz,
        (instance.normal_transform * vec4<f32>(p.w, n.xy, 0.0)).xyz,
        n.zw,
        vec4<f32>((instance.transform * vec4<f32>(tangent.xyz, 0.0)).xyz, sign(tangent.w)), vertex_lightmap(index));
#else
    return Vertex(p.xyz, vec3<f32>(p.w, n.xy), n.zw, geometry_record(frame.geometry.x * 2u + index), vertex_lightmap(index));
#endif
#endif
#endif
}

fn local_corner(triangle: u32, corner: u32) -> vec4<f32> {
    return vec4<f32>(geometry_record(vertex_index(triangle, corner)).xyz, 1.0);
}

fn world_corner(triangle: u32, corner: u32) -> vec4<f32> {
#if LOCAL_INSTANCES
    return resident_instance(triangle, corner).transform * local_corner(triangle, corner);
#else
    return local_corner(triangle, corner);
#endif
}

fn triangle_material(triangle: u32) -> u32 {
#if COMPACT_VERTICES
#if SHADING_WORK
    if ((triangle & CONVENTIONAL_TRIANGLE_BIT) == 0u) { return instances[stream_triangle(triangle).y].material.x; }
#if LOCAL_INSTANCES
    return resident_instance(triangle, 0u).material.x;
#else
    return vertex_owner(vertices[frame.geometry.x + vertex_index(triangle, 0u)].w);
#endif
#else
#if LOCAL_INSTANCES
    return resident_instance(triangle, 0u).material.x;
#else
    return vertex_owner(vertices[frame.geometry.x + vertex_index(triangle, 0u)].w);
#endif
#endif
#else
#if SHADING_WORK
    if ((triangle & CONVENTIONAL_TRIANGLE_BIT) != 0u) {
#if LOCAL_INSTANCES
        return resident_instance(triangle, 0u).material.x;
#else
        return u32(abs(geometry_record(frame.geometry.x * 2u + vertex_index(triangle, 0u)).w)) - 1u;
#endif
    }
    return instances[stream_triangle(triangle).y].material.x;
#else
#if LOCAL_INSTANCES
    return resident_instance(triangle, 0u).material.x;
#else
    return u32(abs(geometry_record(frame.geometry.x * 2u + vertex_index(triangle, 0u)).w)) - 1u;
#endif
#endif
#endif
}
