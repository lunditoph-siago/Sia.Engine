#define_import_path rendering/Lighting/shadow_map

// Resource and projection are supplied by the caller; no pipeline bindings.
fn rendering_shadow_visibility(
    atlas: texture_depth_2d_array,
    depth_sampler: sampler_comparison,
    layer: u32,
    matrix: mat4x4<f32>,
    position: vec3<f32>,
    geometry_normal: vec3<f32>,
    bias: f32
) -> f32 {
    let projected = matrix * vec4<f32>(position, 1.0);
    if (!(projected.w > 0.0) || projected.w > 1e30) { return 1.0; }
    let ndc = projected.xyz / projected.w;
    let uv = ndc.xy * vec2<f32>(0.5, -0.5) + 0.5;
    if (!all(uv >= vec2<f32>(0.0)) || !all(uv <= vec2<f32>(1.0)) || !(ndc.z >= 0.0) || !(ndc.z <= 1.0)) { return 1.0; }
    let size = vec2<i32>(textureDimensions(atlas));
    let pixel = vec2<i32>(uv * vec2<f32>(size));
    // Reference each PCF tap against the geometric receiver plane. Use an
    // analytic projection instead of derivatives, which are unavailable in compute.
    // Degenerate or ill-conditioned projections retain the fixed bias.
    var slope = vec2<f32>(0.0);
    let normal_scale = max(max(abs(geometry_normal.x), abs(geometry_normal.y)), abs(geometry_normal.z));
    if (normal_scale > 0.0 && all(abs(geometry_normal) < vec3<f32>(1e30))) {
        // Scale first: the cross product from screen derivatives can be tiny
        // even for a well-defined plane, or large after world transforms.
        let scaled_normal = geometry_normal / normal_scale;
        let normal = scaled_normal * inverseSqrt(dot(scaled_normal, scaled_normal));
        let axis = select(vec3<f32>(0, 1, 0), vec3<f32>(1, 0, 0), abs(normal.x) < 0.9);
        let tangent = normalize(cross(axis, normal));
        let bitangent = cross(normal, tangent);
        let a = matrix * vec4<f32>(tangent, 0.0);
        let b = matrix * vec4<f32>(bitangent, 0.0);
        // The common factor 1/projected.w cancels from this 2x2 solve.
        let auv = (a.xy - ndc.xy * a.w) * vec2<f32>(0.5, -0.5);
        let buv = (b.xy - ndc.xy * b.w) * vec2<f32>(0.5, -0.5);
        let az = a.z - ndc.z * a.w; let bz = b.z - ndc.z * b.w;
        let scale = max(max(abs(auv.x), abs(auv.y)), max(abs(buv.x), abs(buv.y)));
        if (scale > 1e-20 && scale < 1e18 && abs(az) < 1e18 && abs(bz) < 1e18) {
            let an = auv / scale; let bn = buv / scale;
            let determinant = an.x * bn.y - an.y * bn.x;
            if (abs(determinant) > 1e-6) {
                let candidate = vec2<f32>((az / scale) * bn.y - (bz / scale) * an.y,
                    (bz / scale) * an.x - (az / scale) * bn.x) / determinant;
                if (all(abs(candidate) < vec2<f32>(1e12))) { slope = candidate; }
            }
        }
    }
    var visible = 0.0; var valid = 0u;
    for (var y = -1; y <= 1; y++) {
        for (var x = -1; x <= 1; x++) {
            let at = clamp(pixel + vec2<i32>(x, y), vec2<i32>(0), size - 1);
            let tap_uv = (vec2<f32>(at) + 0.5) / vec2<f32>(size);
            let receiver_depth = ndc.z + dot(slope, tap_uv - uv);
            // Reject extrapolations beyond the shadow camera's depth range,
            // including non-finite values; they cannot represent this receiver.
            if (!(receiver_depth >= 0.0) || !(receiver_depth <= 1.0)) { continue; }
            visible += textureSampleCompareLevel(atlas, depth_sampler, tap_uv, i32(layer), receiver_depth - bias);
            valid += 1u;
        }
    }
    if (valid != 0u) { return visible / f32(valid); }
    let center = clamp(pixel, vec2<i32>(0), size - 1);
    return textureSampleCompareLevel(atlas, depth_sampler, (vec2<f32>(center) + 0.5) / vec2<f32>(size), i32(layer), ndc.z - bias);
}
