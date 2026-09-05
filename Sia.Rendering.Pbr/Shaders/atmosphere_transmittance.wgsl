#import pbr::atmosphere::{atmosphere, sample_medium, sphere_roots}

@group(1) @binding(0) var output_lut: texture_storage_2d<rgba16float, write>;

@compute @workgroup_size(8, 8)
fn compute(@builtin(global_invocation_id) id: vec3<u32>) {
    let size = textureDimensions(output_lut);
    if (any(id.xy >= size)) { return; }
    let uv = vec2<f32>(id.xy) / vec2<f32>(size - 1u);
    let bottom = atmosphere.radii.x;
    let top = atmosphere.radii.y;
    let h = sqrt((top - bottom) * (top + bottom));
    let rho = h * uv.y;
    let radius = sqrt(rho * rho + bottom * bottom);
    let distance = mix(top - radius, rho + h, uv.x);
    var mu = 1.0;
    if (distance > 0.0) { mu = clamp((h * h - rho * rho - distance * distance) / (2.0 * radius * distance), -1.0, 1.0); }
    let origin = vec3<f32>(0.0, radius, 0.0);
    let direction = vec3<f32>(sqrt(max(1.0 - mu * mu, 0.0)), mu, 0.0);
    let length_to_top = max(sphere_roots(origin, direction, top).y, 0.0);
    var optical_depth = vec3<f32>(0.0);
    for (var i = 0u; i < 64u; i += 1u) {
        let dt = length_to_top / 64.0;
        optical_depth += sample_medium(origin + direction * ((f32(i) + 0.5) * dt)).extinction * dt;
    }
    textureStore(output_lut, id.xy, vec4<f32>(exp(-optical_depth), 1.0));
}
