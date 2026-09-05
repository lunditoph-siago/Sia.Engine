#import pbr::atmosphere::{atmosphere, integrate_atmosphere, atmosphere_view_direction}

@group(1) @binding(0) var output_radiance: texture_storage_3d<rgba16float, write>;
@group(1) @binding(1) var output_transmittance: texture_storage_3d<rgba16float, write>;

@compute @workgroup_size(4, 4, 4)
fn compute(@builtin(global_invocation_id) id: vec3<u32>) {
    let size = textureDimensions(output_radiance);
    if (any(id >= size)) { return; }
    let uv = (vec2<f32>(id.xy) + 0.5) / vec2<f32>(size.xy);
    let fraction = f32(id.z) / f32(size.z - 1u);
    let distance = fraction * fraction * atmosphere.radii.w;
    let value = integrate_atmosphere(atmosphere.camera_planet.xyz, atmosphere_view_direction(uv),
        atmosphere.sun.xyz, distance, 32u, false, false);
    textureStore(output_radiance, id, vec4<f32>(min(value.radiance * atmosphere.irradiance.rgb, vec3<f32>(65504.0)), 1.0));
    textureStore(output_transmittance, id, vec4<f32>(value.transmittance, 1.0));
}
