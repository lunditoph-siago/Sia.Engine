#import pbr::scene_lighting::{scene_lighting}

struct SurfaceCamera {
    inverse_view_projection: mat4x4<f32>,
    eye: vec4<f32>,
    mode: vec4<u32>,
};

@group(0) @binding(0) var<uniform> surface_camera: SurfaceCamera;
@group(0) @binding(1) var depth: texture_depth_2d;
@group(0) @binding(2) var base_roughness: texture_2d<f32>;
@group(0) @binding(3) var normal_metallic: texture_2d<f32>;
@group(0) @binding(4) var emissive_occlusion: texture_2d<f32>;
@group(0) @binding(5) var debug_color: texture_2d<f32>;

@vertex
fn vertex(@builtin(vertex_index) index: u32) -> @builtin(position) vec4<f32> {
    let xy = vec2<f32>(f32((index << 1u) & 2u), f32(index & 2u));
    return vec4<f32>(xy * 2.0 - 1.0, 0.0, 1.0);
}

@fragment
fn fragment(@builtin(position) position: vec4<f32>) -> @location(0) vec4<f32> {
    let pixel = vec2<i32>(position.xy);
    let z = textureLoad(depth, pixel, 0);
    if (z >= 1.0) { discard; }
    if (surface_camera.mode.x != 0u) { return textureLoad(debug_color, pixel, 0); }
    let size = vec2<f32>(textureDimensions(depth));
    let ndc = position.xy / size * vec2<f32>(2.0, -2.0) + vec2<f32>(-1.0, 1.0);
    let world_h = surface_camera.inverse_view_projection * vec4<f32>(ndc, z, 1.0);
    let world_position = world_h.xyz / world_h.w;
    let base = textureLoad(base_roughness, pixel, 0);
    let normal = textureLoad(normal_metallic, pixel, 0);
    let emissive = textureLoad(emissive_occlusion, pixel, 0);
    let color = scene_lighting(world_position, normalize(normal.xyz), normalize(surface_camera.eye.xyz - world_position),
        base.rgb, normal.w, base.w, emissive.rgb, emissive.w, position.xy);
    return vec4<f32>(color, 1.0);
}
