#import pbr::atmosphere::{atmosphere, integrate_atmosphere, sky_angles}

@group(1) @binding(0) var output_lut: texture_storage_2d<rgba16float, write>;

@compute @workgroup_size(8, 8)
fn compute(@builtin(global_invocation_id) id: vec3<u32>) {
    let size = textureDimensions(output_lut);
    if (any(id.xy >= size)) { return; }
    let uv = vec2<f32>(id.xy) / vec2<f32>(size - 1u);
    let radius = length(atmosphere.camera_planet.xyz);
    let angles = sky_angles(uv, radius);
    let sin_zenith = sqrt(max(1.0 - angles.x * angles.x, 0.0));
    let direction = vec3<f32>(sin_zenith * angles.y, angles.x, sin_zenith * sqrt(max(1.0 - angles.y * angles.y, 0.0)));
    let mu_sun = dot(normalize(atmosphere.camera_planet.xyz), atmosphere.sun.xyz);
    let sun = vec3<f32>(sqrt(max(1.0 - mu_sun * mu_sun, 0.0)), mu_sun, 0.0);
    let value = integrate_atmosphere(vec3<f32>(0.0, radius, 0.0), direction, sun, 1e9, 64u, false, true);
    textureStore(output_lut, id.xy, vec4<f32>(min(value.radiance * atmosphere.irradiance.rgb, vec3<f32>(65504.0)), 1.0));
}
