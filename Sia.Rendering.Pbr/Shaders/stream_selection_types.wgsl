#define_import_path pbr/stream_selection_types

struct StreamSelection {
    vp: mat4x4<f32>,
    table: vec4<u32>, // root entries base, root count, page count, refinement limit
    screen: vec4<f32>, // width, height, error, unused
    output: vec4<u32>, // single capacity, double capacity, shadow layer, unused
    eye_near: vec4<f32>,
    forward_pixels: vec4<f32>
}
