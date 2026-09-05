#import pbr::atmosphere::{atmosphere, atmosphere_sampler, atmosphere_view_direction, integrate_atmosphere}
#import pbr::ibl::{ibl_fullscreen_ndc}

@group(1) @binding(0) var scene: texture_2d<f32>;
@group(1) @binding(1) var depth: texture_depth_2d;
@group(1) @binding(2) var aerial_radiance: texture_3d<f32>;
@group(1) @binding(3) var aerial_transmittance: texture_3d<f32>;

struct VertexOutput {
    @builtin(position) position: vec4<f32>,
};

@vertex
fn vertex(@builtin(vertex_index) index: u32) -> VertexOutput {
    return VertexOutput(vec4<f32>(ibl_fullscreen_ndc(index), 0.0, 1.0));
}

@fragment
fn fragment(input: VertexOutput) -> @location(0) vec4<f32> {
    let pixel = vec2<i32>(input.position.xy);
    let original = textureLoad(scene, pixel, 0);
    let z = textureLoad(depth, pixel, 0);
    if (z >= 1.0) { return original; }
    let uv = input.position.xy / vec2<f32>(textureDimensions(scene));
    let ndc = uv * vec2<f32>(2.0, -2.0) + vec2<f32>(-1.0, 1.0);
    let h = atmosphere.inverse_view_projection * vec4<f32>(ndc, z, 1.0);
    let distance = length(h.xyz / h.w - atmosphere.camera_world.xyz) * atmosphere.radii.z;
    var radiance: vec3<f32>;
    var transmittance: vec3<f32>;
    if (distance > atmosphere.radii.w || length(atmosphere.camera_planet.xyz) >= atmosphere.radii.y) {
        let value = integrate_atmosphere(atmosphere.camera_planet.xyz, atmosphere_view_direction(uv), atmosphere.sun.xyz, distance, 64u, false, false);
        radiance = value.radiance * atmosphere.irradiance.rgb;
        transmittance = value.transmittance;
    } else {
        let slices = f32(textureDimensions(aerial_radiance).z);
        let depth_coordinate = max(distance / atmosphere.radii.w, 0.0) * (slices - 1.0) * (slices - 1.0);
        let lower = floor(sqrt(depth_coordinate));
        let slice = lower + (depth_coordinate - lower * lower) / (2.0 * lower + 1.0);
        let coord = vec3<f32>(uv, (slice + 0.5) / slices);
        radiance = textureSampleLevel(aerial_radiance, atmosphere_sampler, coord, 0.0).rgb;
        transmittance = textureSampleLevel(aerial_transmittance, atmosphere_sampler, coord, 0.0).rgb;
    }
    return vec4<f32>(min(original.rgb * transmittance + radiance, vec3<f32>(65504.0)), original.a);
}
