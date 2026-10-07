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
    var result = sample_material(m, wa.xyz * bary.x + wb.xyz * bary.y + wc.xyz * bary.z, normal, vec4<f32>(t, tangent.w), uv, uv_dx, uv_dy, dot(cross(wb.xyz - wa.xyz, wc.xyz - wa.xyz), frame.eye.xyz - wa.xyz) >= 0.0);
    result.lightmap = a.lightmap * bary.x + b.lightmap * bary.y + c.lightmap * bary.z;
    result.lightmap_dx = (a.lightmap.xy * dx.x + b.lightmap.xy * dx.y + c.lightmap.xy * dx.z
        - result.lightmap.xy * dot(dx, vec3<f32>(1.0))) / denominator;
    result.lightmap_dy = (a.lightmap.xy * dy.x + b.lightmap.xy * dy.y + c.lightmap.xy * dy.z
        - result.lightmap.xy * dot(dy, vec3<f32>(1.0))) / denominator;
    return result;
}
