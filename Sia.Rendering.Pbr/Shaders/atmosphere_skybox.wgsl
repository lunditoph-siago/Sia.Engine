#import pbr::atmosphere::{atmosphere, sky_radiance, atmosphere_view_direction, ground_distance, transmittance_to_sun, sphere_roots, integrate_atmosphere}
#import pbr::ibl::{ibl_fullscreen_ndc}

struct VertexOutput {
    @builtin(position) position: vec4<f32>,
    @location(0) uv: vec2<f32>,
};

@vertex
fn vertex(@builtin(vertex_index) index: u32) -> VertexOutput {
    let ndc = ibl_fullscreen_ndc(index);
    return VertexOutput(vec4<f32>(ndc, 0.0, 1.0), ndc * vec2<f32>(0.5, -0.5) + 0.5);
}

@fragment
fn fragment(input: VertexOutput) -> @location(0) vec4<f32> {
    let direction = atmosphere_view_direction(input.uv);
    var color = sky_radiance(direction);
    if (length(atmosphere.camera_planet.xyz) >= atmosphere.radii.y) {
        color = integrate_atmosphere(atmosphere.camera_planet.xyz, direction, atmosphere.sun.xyz, 1e9, 64u, false, true).radiance * atmosphere.irradiance.rgb;
    }
    let difference = direction - atmosphere.sun.xyz;
    let chord_squared = dot(difference, difference);
    let edge = max(fwidth(chord_squared) * 0.5, 1e-10);
    let disk = 1.0 - smoothstep(atmosphere.sun.w - edge, atmosphere.sun.w + edge, chord_squared);
    if (disk > 0.0 && ground_distance(atmosphere.camera_planet.xyz, direction) < 0.0) {
        var position = atmosphere.camera_planet.xyz;
        var transmittance = vec3<f32>(1.0);
        let top = sphere_roots(position, direction, atmosphere.radii.y);
        if (top.y > 0.0) {
            position += direction * max(top.x, 0.0);
            transmittance = transmittance_to_sun(position, direction);
        }
        color += transmittance * atmosphere.irradiance.rgb * disk / atmosphere.irradiance.w;
    }
    return vec4<f32>(min(color, vec3<f32>(65504.0)), 1.0);
}
