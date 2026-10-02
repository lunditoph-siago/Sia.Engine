#define_import_path pbr/frame_types

struct Directional {
    direction: vec4<f32>,
    radiance: vec4<f32>
}

struct Frame {
    vp: mat4x4<f32>,
    view: mat4x4<f32>,
    inv_proj: mat4x4<f32>,
    inv_vp: mat4x4<f32>,
    eye: vec4<f32>,
    size: vec4<u32>,
    geometry: vec4<u32>,
    grid: vec4<u32>,
    depth: vec4<f32>,
    counts: vec4<u32>,
    shadow: vec4<u32>,
    splits: vec4<f32>,
    directional: array<Directional, 4>,
}
