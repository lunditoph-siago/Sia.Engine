#define_import_path pbr/Common/reconstruction

#import pbr/Common/surface

fn surface_at(ordinal: u32, pixel: vec2<f32>) -> Surface {
    let m = materials[triangle_material(ordinal)];
    let a = vertex_data(ordinal, 0u);
    let b = vertex_data(ordinal, 1u);
    let c = vertex_data(ordinal, 2u);
    let wa = vec4<f32>(a.position, 1.0);
    let wb = vec4<f32>(b.position, 1.0);
    let wc = vec4<f32>(c.position, 1.0);
    let ca = frame.vp * wa;
    let cb = frame.vp * wb;
    let cc = frame.vp * wc;
    let fa = cross(cb.xyw, cc.xyw);
    let fb = cross(cc.xyw, ca.xyw);
    let fc = cross(ca.xyw, cb.xyw);
    let q = vec3<f32>(pixel / vec2<f32>(frame.size.xy) * vec2<f32>(2.0, -2.0) + vec2<f32>(-1.0, 1.0), 1.0);
    let weights = vec3<f32>(dot(fa, q), dot(fb, q), dot(fc, q));
    let denominator = dot(weights, vec3<f32>(1.0));
    var surface: Surface;
    surface.valid = abs(denominator) >= 1e-20;
    if (!surface.valid) {
        return surface;
    }
    let bary = weights / denominator;
    let dx = vec3<f32>(fa.x, fb.x, fc.x) * (2.0 / f32(frame.size.x));
    let dy = vec3<f32>(fa.y, fb.y, fc.y) * (-2.0 / f32(frame.size.y));
    let uv = a.uv * bary.x + b.uv * bary.y + c.uv * bary.z;
    let uv_dx = (a.uv * dx.x + b.uv * dx.y + c.uv * dx.z - uv * dot(dx, vec3<f32>(1.0))) / denominator;
    let uv_dy = (a.uv * dy.x + b.uv * dy.y + c.uv * dy.z - uv * dot(dy, vec3<f32>(1.0))) / denominator;
    let normal = a.normal * bary.x + b.normal * bary.y + c.normal * bary.z;
    let tangent = a.tangent * bary.x + b.tangent * bary.y + c.tangent * bary.z;
    let t = tangent.xyz;
    let geometry_normal = cross(wb.xyz - wa.xyz, wc.xyz - wa.xyz);
    surface = sample_material(m, wa.xyz * bary.x + wb.xyz * bary.y + wc.xyz * bary.z, normal, vec4<f32>(t, tangent.w), uv, uv_dx, uv_dy, dot(geometry_normal, frame.eye.xyz - wa.xyz) >= 0.0);
    surface.geometry_normal = geometry_normal;
    surface.bake_uv = a.bake_uv * bary.x + b.bake_uv * bary.y + c.bake_uv * bary.z;
    surface.bake_uv_dx = (a.bake_uv * dx.x + b.bake_uv * dx.y + c.bake_uv * dx.z - surface.bake_uv * dot(dx, vec3<f32>(1.0))) / denominator;
    surface.bake_uv_dy = (a.bake_uv * dy.x + b.bake_uv * dy.y + c.bake_uv * dy.z - surface.bake_uv * dot(dy, vec3<f32>(1.0))) / denominator;
    surface.bake_present = a.bake_present && b.bake_present && c.bake_present
        && a.source_instance == b.source_instance && a.source_instance == c.source_instance;
    surface.source_instance = a.source_instance;
    return surface;
}
