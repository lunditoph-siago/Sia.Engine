#define_import_path pbr/Common/triangle_debug

#import pbr/bindings

// Debug palette only: never use this hash as temporal geometry identity.
// Local positions survive work compaction and page-arena relocation.
fn triangle_debug_color(ordinal: u32) -> vec3<f32> {
    var hash = 2166136261u;
#if SHADING_WORK
    if ((ordinal & CONVENTIONAL_TRIANGLE_BIT) == 0u) {
        hash = (hash ^ (stream_triangle(ordinal).y + 1u)) * 16777619u;
    }
#endif
    for (var corner = 0u; corner < 3u; corner++) {
        let position = geometry_record(vertex_index(ordinal, corner)).xyz;
        let bits = bitcast<vec3<u32>>(position);
        hash = (hash ^ bits.x) * 16777619u;
        hash = (hash ^ bits.y) * 16777619u;
        hash = (hash ^ bits.z) * 16777619u;
    }
    hash = (hash ^ (hash >> 16u)) * 0x7feb352du;
    hash = (hash ^ (hash >> 15u)) * 0x846ca68bu;
    hash ^= hash >> 16u;
    let color = vec3<f32>(f32(hash & 255u), f32((hash >> 8u) & 255u), f32(hash >> 24u)) / 255.0;
    return color * 0.8 + 0.2;
}
