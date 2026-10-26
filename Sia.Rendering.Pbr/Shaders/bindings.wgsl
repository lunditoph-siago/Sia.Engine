#define_import_path pbr/bindings

#import pbr/frame_types
#import pbr/stream_work_types

struct Material {
    color: vec4<f32>,
    emission: vec4<f32>,
    factors: vec4<f32>,
    layers: vec4<u32>,
    indices: vec4<u32>,
    transport: vec4<f32>,
}

@group(0) @binding(0) var<uniform> frame: Frame;
@group(0) @binding(1) var<storage, read> scene_data: array<vec4<f32>>;
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
#if REFLECTION_CAPTURES
@group(0) @binding(15) var capture_environment: texture_2d_array<f32>;
@group(0) @binding(16) var<uniform> capture_regions: array<vec4<f32>, #{CAPTURE_REGION_ROWS}>;
#endif
#if SCENE_GI
@group(0) @binding(9) var probe_volume: texture_3d<f32>;
@group(0) @binding(10) var<uniform> probe_header: array<vec4<f32>,3>;
#endif
// Lighting adds sampled integer textures rather than storage buffers.
#if STATIC_LIGHTMAP
@group(0) @binding(12) var lightmap_metadata: texture_2d<u32>;
@group(0) @binding(13) var lightmap_mapping: texture_2d<u32>;
@group(0) @binding(14) var lightmap_pages: texture_2d_array<u32>;
#endif
@group(1) @binding(0) var<storage, read> vertices: array<u32>;
@group(1) @binding(1) var<storage, read> topology: array<u32>;
#if RASTER_POSITION_TABLES
@group(1) @binding(3) var<storage, read> vertex_attributes: array<u32>;
#endif
#if STREAM_INSTANCES || RESIDENT_INSTANCES
#import pbr/instance_types
@group(1) @binding(2) var<storage, read> instances: array<Instance>;
#endif
#if RESIDENT_INSTANCES || CPU_STREAM_INSTANCES
// Rows are sorted by their nonoverlapping logical triangle intervals.
fn resident_instance(triangle: u32) -> u32 {
    var first = 0u;
#if CPU_STREAM_INSTANCES
    var end = frame.geometry.z / frame.geometry.w;
#else
    var end = arrayLength(&instances);
#endif
    loop {
        if (first >= end) { break; }
        let middle = first + (end - first) / 2u;
        let row = instances[middle].material;
        if (triangle < row.z) { end = middle; }
        else if (triangle - row.z >= row.w) { first = middle + 1u; }
        else { return middle; }
    }
    return 0u;
}
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

fn resident_vec4(offset: u32) -> vec4<u32> {
    return vec4<u32>(vertices[offset], vertices[offset+1u], vertices[offset+2u], vertices[offset+3u]);
}

fn vertex_plane(index: u32) -> vec4<f32> {
    return bitcast<vec4<f32>>(resident_vec4(index * 4u));
}

fn packed_direction(word: u32) -> vec3<f32> {
    let oct = unpack2x16snorm(word);
    var direction = vec3<f32>(oct, 1.0 - abs(oct.x) - abs(oct.y));
    let fold = clamp(-direction.z, 0.0, 1.0);
    direction = vec3<f32>(direction.xy + select(vec2<f32>(fold), vec2<f32>(-fold), direction.xy >= vec2<f32>(0.0)), direction.z);
    return direction;
}

fn resident_direction(word: u32) -> vec3<f32> {
    return unit(packed_direction(word));
}

fn stream_direction(word: u32, scale: u32) -> vec3<f32> {
    let direction = vec3<f32>(unpack2x16snorm(word), unpack2x16snorm(scale).x) * 2.0;
    var magnitude = bitcast<f32>(scale & 0xffff0000u);
    if ((scale & 0x80000000u) != 0u) {
        magnitude = bitcast<f32>(1u << ((scale >> 16u) & 31u));
    }
    return direction * magnitude;
}

fn resident_normal(word: u32) -> vec3<f32> {
    return select(resident_direction(word), vec3<f32>(0.0), word == 0x80008000u);
}

fn resident_tangent(words: vec2<u32>) -> vec4<f32> {
    let magnitude = bitcast<f32>(words.y & 0xfffffffcu);
    return vec4<f32>(resident_direction(words.x) * magnitude, select(1.0, -1.0, (words.y & 1u) != 0u));
}

fn shadow_matrix(layer: u32) -> mat4x4<f32> {
    let i = #{SHADOW_MATRIX_BASE}u + layer * 4u;
    return mat4x4<f32>(scene_data[i], scene_data[i + 1u], scene_data[i + 2u], scene_data[i + 3u]);
}

fn vertex_index(triangle: u32, corner: u32) -> u32 {
#if SHADING_WORK
    return topology[stream_triangle(triangle).x * 3u + corner];
#elif RESIDENT_INSTANCES
    let instance = instances[resident_instance(triangle)];
    return topology[(instance.geometry.x + triangle - instance.material.z) * 3u + corner];
#elif CPU_STREAM_INSTANCES
    return topology[(triangle - instances[resident_instance(triangle)].material.z) * 3u + corner];
#else
    return topology[triangle * 3u + corner];
#endif
}

struct Vertex {
    position: vec3<f32>,
    normal: vec3<f32>,
    uv: vec2<f32>,
    tangent: vec4<f32>,
    bake_uv: vec2<f32>,
    bake_present: bool,
    source_instance: u32,
}

fn stream_vertex_value(index: u32) -> Vertex {
    let p = vertex_plane(index);
    let n = resident_vec4(frame.geometry.x * 4u + index * 4u);
    let t = resident_vec4(frame.geometry.x * 8u + index * 4u);
    let metadata = vertices[frame.geometry.x * 12u + index];
    return Vertex(p.xyz, stream_direction(n.x, n.y), vec2<f32>(p.w, bitcast<f32>(n.z)),
        vec4<f32>(stream_direction(t.x, t.y), bitcast<f32>(t.z)), bitcast<vec2<f32>>(vec2<u32>(n.w, t.w)),
        (metadata & 0x80000000u) != 0u, metadata & 0x7fffffffu);
}

#if RASTER_POSITION_TABLES
fn raster_position(index: u32) -> vec3<f32> {
    var offset = index * 5u;
    if (frame.geometry.y == 2u) { offset = frame.geometry.x * 3u + vertices[index * 3u] * 3u; }
    return bitcast<vec3<f32>>(vec3<u32>(vertices[offset], vertices[offset + 1u], vertices[offset + 2u]));
}

fn raster_vertex_value(index: u32, row: u32) -> Vertex {
    var uv_offset = index * 5u + 3u;
    var attribute_offset = index * 5u;
    if (frame.geometry.y == 2u) { uv_offset = index * 3u + 1u; attribute_offset = index * 4u; }
    let normal = resident_normal(vertex_attributes[attribute_offset]);
    let flags = vertex_attributes[attribute_offset + 2u];
    let tangent = resident_tangent(vec2<u32>(vertex_attributes[attribute_offset + 1u], flags));
    var bake = vec2<u32>(0u);
    if (frame.geometry.y == 2u) {
        let packed = vertex_attributes[attribute_offset + 3u]; let base = frame.geometry.x * 4u;
        bake = vec2<u32>(vertex_attributes[base + (packed & 65535u)], vertex_attributes[base + (packed >> 16u)]);
    }
    else { bake = vec2<u32>(vertex_attributes[attribute_offset + 3u], vertex_attributes[attribute_offset + 4u]); }
    let instance = instances[row];
    return Vertex((instance.transform * vec4<f32>(raster_position(index), 1.0)).xyz,
        (instance.normal_transform * vec4<f32>(normal, 0.0)).xyz,
        bitcast<vec2<f32>>(vec2<u32>(vertices[uv_offset], vertices[uv_offset + 1u])),
        vec4<f32>((instance.transform * vec4<f32>(tangent.xyz, 0.0)).xyz, tangent.w),
        bitcast<vec2<f32>>(bake), (flags & 2u) != 0u, instance.material.y);
}
#endif

fn vertex_data(triangle: u32, corner: u32) -> Vertex {
    let index = vertex_index(triangle, corner);
#if RASTER_POSITION_TABLES
    return raster_vertex_value(index, resident_instance(triangle));
#else
    let p = vertex_plane(index);
#if RESIDENT_INSTANCES
    let n = resident_vec4(frame.geometry.x * 4u + index * 4u);
    let offset = frame.geometry.x * 8u + index * 2u;
    let words = vec2<u32>(vertices[offset], vertices[offset+1u]);
    let instance = instances[resident_instance(triangle)];
    let tangent = resident_tangent(words);
    return Vertex(
        (instance.transform * vec4<f32>(p.xyz, 1.0)).xyz,
        (instance.normal_transform * vec4<f32>(resident_normal(n.y), 0.0)).xyz,
        vec2<f32>(p.w, bitcast<f32>(n.x)),
        vec4<f32>((instance.transform * vec4<f32>(tangent.xyz, 0.0)).xyz, tangent.w),
        bitcast<vec2<f32>>(n.zw), (words.y & 2u) != 0u, instance.material.y);
#else
    let value = stream_vertex_value(index);
#if SHADING_WORK || CPU_STREAM_INSTANCES
#if CPU_STREAM_INSTANCES
    let row = resident_instance(triangle);
#else
    let row = stream_triangle(triangle).y;
#endif
    let instance = instances[row];
    return Vertex(
        (instance.transform * vec4<f32>(value.position, 1.0)).xyz,
        (instance.normal_transform * vec4<f32>(value.normal, 0.0)).xyz,
        value.uv,
        vec4<f32>((instance.transform * vec4<f32>(value.tangent.xyz, 0.0)).xyz, value.tangent.w),
        value.bake_uv, value.bake_present, row);
#else
    return value;
#endif
#endif
#endif
}

fn world_corner(triangle: u32, corner: u32) -> vec4<f32> {
#if RESIDENT_INSTANCES || CPU_STREAM_INSTANCES
#if RASTER_POSITION_TABLES
    return instances[resident_instance(triangle)].transform * vec4<f32>(raster_position(vertex_index(triangle, corner)), 1.0);
#else
    return instances[resident_instance(triangle)].transform * vec4<f32>(vertex_plane(vertex_index(triangle, corner)).xyz, 1.0);
#endif
#else
    return vec4<f32>(vertex_plane(vertex_index(triangle, corner)).xyz, 1.0);
#endif
}

fn triangle_material(triangle: u32) -> u32 {
#if SHADING_WORK
    return instances[stream_triangle(triangle).y].material.x;
#elif RESIDENT_INSTANCES || CPU_STREAM_INSTANCES
    return instances[resident_instance(triangle)].material.x;
#else
    return u32(abs(bitcast<f32>(vertices[frame.geometry.x * 8u + vertex_index(triangle, 0u) * 4u + 2u]))) - 1u;
#endif
}
