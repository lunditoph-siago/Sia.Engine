#define_import_path pbr/frame_types

struct Directional {
    direction: vec4<f32>, // xyz light-ray direction; w directional constant receiver bias in world units
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
    depth: vec4<f32>, // logarithmic cluster scale/bias, physical-to-HDR factor, stored-cube-to-physical factor
    counts: vec4<u32>,
    shadow: vec4<u32>,
    splits: vec4<f32>, // xyz cascade distances; w directional slope receiver bias in world units
    directional: array<Directional, 4>,
}
