#define_import_path rendering/Lighting/shadow_map

// Resource and projection are supplied by the caller; no pipeline bindings.
struct RenderingShadowReceiver {
    ndc: vec3<f32>,
    uv: vec2<f32>,
    slope: vec2<f32>,
    valid: bool,
}

fn rendering_shadow_receiver(matrix: mat4x4<f32>, position: vec3<f32>, geometry_normal: vec3<f32>) -> RenderingShadowReceiver {
    var receiver: RenderingShadowReceiver;
    let projected = matrix * vec4<f32>(position, 1.0);
    if (!(projected.w > 0.0) || projected.w > 1e30) { return receiver; }
    let ndc = projected.xyz / projected.w;
    let uv = ndc.xy * vec2<f32>(0.5, -0.5) + 0.5;
    if (!all(uv >= vec2<f32>(0.0)) || !all(uv <= vec2<f32>(1.0)) || !(ndc.z >= 0.0) || !(ndc.z <= 1.0)) { return receiver; }
    receiver.ndc = ndc;
    receiver.uv = uv;
    receiver.valid = true;
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
    receiver.slope = slope;
    return receiver;
}

fn rendering_shadow_weights(fraction: f32) -> vec4<f32> {
    // A three-wide box of bilinear comparisons has four unique texels per
    // axis. Keep the subtexel fraction instead of snapping the entire kernel.
    return vec4<f32>(1.0 - fraction, 1.0, 1.0, fraction);
}

fn rendering_shadow_reference(receiver: RenderingShadowReceiver, tap_uv: vec2<f32>) -> f32 {
    return receiver.ndc.z + dot(receiver.slope, tap_uv - receiver.uv);
}

fn rendering_shadow_fallback(atlas: texture_depth_2d_array, depth_sampler: sampler_comparison,
    layer: u32, receiver: RenderingShadowReceiver, size: vec2<i32>, bias: f32) -> f32 {
    let center = clamp(vec2<i32>(receiver.uv * vec2<f32>(size)), vec2<i32>(0), size - 1);
    return textureSampleCompareLevel(atlas, depth_sampler, (vec2<f32>(center) + 0.5) / vec2<f32>(size), i32(layer), receiver.ndc.z - bias);
}

// Scalar reference preserves the original callable signature and comparison
// sampler semantics. Every texel keeps its own receiver-plane reference.
fn rendering_shadow_visibility(atlas: texture_depth_2d_array, depth_sampler: sampler_comparison,
    layer: u32, matrix: mat4x4<f32>, position: vec3<f32>, geometry_normal: vec3<f32>, bias: f32) -> f32 {
    let receiver = rendering_shadow_receiver(matrix, position, geometry_normal);
    if (!receiver.valid) { return 1.0; }
    let size = vec2<i32>(textureDimensions(atlas));
    let texel = receiver.uv * vec2<f32>(size) - 0.5;
    let base = vec2<i32>(floor(texel)); let fraction = fract(texel);
    let wx = rendering_shadow_weights(fraction.x); let wy = rendering_shadow_weights(fraction.y);
    var visible = 0.0; var valid_weight = 0.0;
    for (var y = 0u; y < 4u; y++) {
        for (var x = 0u; x < 4u; x++) {
            let weight = wx[x] * wy[y];
            if (!(weight > 0.0)) { continue; }
            let at = clamp(base + vec2<i32>(i32(x) - 1, i32(y) - 1), vec2<i32>(0), size - 1);
            let tap_uv = (vec2<f32>(at) + 0.5) / vec2<f32>(size);
            let receiver_depth = rendering_shadow_reference(receiver, tap_uv);
            // Reject extrapolations beyond the shadow camera's depth range,
            // including non-finite values; they cannot represent this receiver.
            if (!(receiver_depth >= 0.0) || !(receiver_depth <= 1.0)) { continue; }
            visible += weight * textureSampleCompareLevel(atlas, depth_sampler, tap_uv, i32(layer), receiver_depth - bias);
            valid_weight += weight;
        }
    }
    if (valid_weight > 0.0) { return visible / valid_weight; }
    return rendering_shadow_fallback(atlas, depth_sampler, layer, receiver, size, bias);
}

// raw_sampler must use a NonFiltering layout, nearest filtering, and clamp both
// axes for the depth texture. Gather reads level0. WGSL xyzw order is
// (x0,y1),(x1,y1),(x1,y0),(x0,y0).
fn rendering_shadow_gather_depths(atlas: texture_depth_2d_array, raw_sampler: sampler, layer: u32,
    base: vec2<i32>, size: vec2<i32>) -> array<vec4<f32>, 4> {
    let dimensions = vec2<f32>(size);
    let origin = vec2<f32>(base);
    return array<vec4<f32>, 4>(
        textureGather(atlas, raw_sampler, origin / dimensions, i32(layer)),
        textureGather(atlas, raw_sampler, (origin + vec2<f32>(2, 0)) / dimensions, i32(layer)),
        textureGather(atlas, raw_sampler, (origin + vec2<f32>(0, 2)) / dimensions, i32(layer)),
        textureGather(atlas, raw_sampler, (origin + vec2<f32>(2, 2)) / dimensions, i32(layer)));
}

fn rendering_shadow_gather_value(depths: array<vec4<f32>, 4>, x: u32, y: u32) -> f32 {
    let block = (y / 2u) * 2u + x / 2u;
    let component = select(3u - (x & 1u), x & 1u, (y & 1u) != 0u);
    return depths[block][component];
}

// Production LessEqual path: four raw gathers replace sixteen scalar fetches.
// Comparisons still use each texel's plane depth; filtering the depth/reference
// first would reintroduce errors on grazing receivers.
fn rendering_shadow_visibility_gather(atlas: texture_depth_2d_array, depth_sampler: sampler_comparison,
    raw_sampler: sampler, layer: u32, matrix: mat4x4<f32>, position: vec3<f32>, geometry_normal: vec3<f32>, bias: f32) -> f32 {
    let receiver = rendering_shadow_receiver(matrix, position, geometry_normal);
    if (!receiver.valid) { return 1.0; }
    let size = vec2<i32>(textureDimensions(atlas));
    let texel = receiver.uv * vec2<f32>(size) - 0.5;
    let base = vec2<i32>(floor(texel)); let fraction = fract(texel);
    let wx = rendering_shadow_weights(fraction.x); let wy = rendering_shadow_weights(fraction.y);
    let depths = rendering_shadow_gather_depths(atlas, raw_sampler, layer, base, size);
    var visible = 0.0; var valid_weight = 0.0;
    for (var y = 0u; y < 4u; y++) {
        for (var x = 0u; x < 4u; x++) {
            let weight = wx[x] * wy[y];
            if (!(weight > 0.0)) { continue; }
            let at = clamp(base + vec2<i32>(i32(x) - 1, i32(y) - 1), vec2<i32>(0), size - 1);
            let tap_uv = (vec2<f32>(at) + 0.5) / vec2<f32>(size);
            let receiver_depth = rendering_shadow_reference(receiver, tap_uv);
            if (!(receiver_depth >= 0.0) || !(receiver_depth <= 1.0)) { continue; }
            let stored = rendering_shadow_gather_value(depths, x, y);
            visible += weight * select(0.0, 1.0, receiver_depth - bias <= stored);
            valid_weight += weight;
        }
    }
    if (valid_weight > 0.0) { return visible / valid_weight; }
    return rendering_shadow_fallback(atlas, depth_sampler, layer, receiver, size, bias);
}
