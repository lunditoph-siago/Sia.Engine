#define_import_path rendering/Lighting/shadow_map

// Resource and projection are supplied by the caller; no pipeline bindings.
fn rendering_shadow_visibility(
    atlas: texture_depth_2d_array,
    layer: u32,
    matrix: mat4x4<f32>,
    position: vec3<f32>,
    bias: f32
) -> f32 {
    let projected = matrix * vec4<f32>(position, 1.0);
    if (projected.w <= 0.0) { return 1.0; }
    let ndc = projected.xyz / projected.w;
    let uv = ndc.xy * vec2<f32>(0.5, -0.5) + 0.5;
    if (any(uv < vec2<f32>(0.0)) || any(uv > vec2<f32>(1.0)) || ndc.z < 0.0 || ndc.z > 1.0) { return 1.0; }
    let size = vec2<i32>(textureDimensions(atlas));
    let pixel = vec2<i32>(uv * vec2<f32>(size));
    var visible = 0.0;
    for (var y = -1; y <= 1; y++) {
        for (var x = -1; x <= 1; x++) {
            let at = clamp(pixel + vec2<i32>(x, y), vec2<i32>(0), size - 1);
            visible += select(0.0, 1.0, ndc.z - bias <= textureLoad(atlas, at, i32(layer), 0));
        }
    }
    return visible / 9.0;
}
