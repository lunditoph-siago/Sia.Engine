#define_import_path pbr/Providers/lightmap

#import pbr/Common/surface

fn lightmap_meta(at: u32) -> vec4<u32> {
    let width = textureDimensions(lightmap_metadata).x;
    return textureLoad(lightmap_metadata, vec2<i32>(i32(at % width), i32(at / width)), 0);
}

fn lightmap_float(at: u32) -> vec3<f32> { return bitcast<vec3<f32>>(lightmap_meta(at).xyz); }

fn lightmap_pair(word: u32) -> vec2<f32> {
    return vec2<f32>(f32(bitcast<i32>(word << 16u) >> 16), f32(bitcast<i32>(word) >> 16));
}

fn lightmap_coefficients(packed0: vec4<u32>, packed1: vec4<u32>, scales: array<vec3<f32>, 4>) -> array<vec3<f32>, 4> {
    let a = lightmap_pair(packed0.x); let b = lightmap_pair(packed0.y); let c = lightmap_pair(packed0.z);
    let d = lightmap_pair(packed0.w); let e = lightmap_pair(packed1.x); let f = lightmap_pair(packed1.y);
    let codes = array<vec3<f32>, 4>(vec3<f32>(a, b.x), vec3<f32>(b.y, c), vec3<f32>(d, e.x), vec3<f32>(e.y, f));
    var decoded: array<vec3<f32>, 4>;
    for (var component = 0u; component < 4u; component++) {
        decoded[component] = clamp(codes[component] * scales[component], vec3<f32>(-1e20), vec3<f32>(1e20));
    }
    return decoded;
}

fn lightmap_evaluate(coefficients: array<vec3<f32>, 4>, normal: vec3<f32>) -> vec3<f32> {
    return max(coefficients[0] * 0.886226925
        + (coefficients[1] * normal.y + coefficients[2] * normal.z + coefficients[3] * normal.x) * 1.02332671, vec3<f32>(0));
}

// w indicates valid lighting, including covered black. An absent receiver/chart
// leaves diffuse selection to the existing environment/probe provider.
fn lightmap_irradiance(surface: Surface) -> vec4<f32> {
    let rootCounts = lightmap_meta(0u); let rootOffsets = lightmap_meta(1u);
    if (!surface.bake_present || surface.source_instance >= rootCounts.x
        || !all(surface.bake_uv >= vec2<f32>(0)) || !all(surface.bake_uv < vec2<f32>(1))) { return vec4<f32>(0); }
    let receiverPlusOne = lightmap_meta(rootOffsets.x + surface.source_instance / 4u)[surface.source_instance % 4u];
    if (receiverPlusOne == 0u || receiverPlusOne > rootCounts.y) { return vec4<f32>(0); }
    let rootAt = rootOffsets.y + (receiverPlusOne - 1u) * 7u;
    let rootReceiver = lightmap_meta(rootAt);
    let scales = array<vec3<f32>, 4>(lightmap_float(rootAt + 1u), lightmap_float(rootAt + 2u),
        lightmap_float(rootAt + 3u), lightmap_float(rootAt + 4u));
    let support = lightmap_coefficients(lightmap_meta(rootAt + 5u), lightmap_meta(rootAt + 6u), scales);
    // Valid receiver-wide support remains available before member metadata IO.
    if (rootReceiver.y == 0u) { return vec4<f32>(lightmap_evaluate(support, surface.normal), 1); }
    let member = rootReceiver.y;
    let counts = lightmap_meta(member); let offsets = lightmap_meta(member + 1u);
    if (rootReceiver.z >= counts.y) { return vec4<f32>(lightmap_evaluate(support, surface.normal), 1); }
    let receiverAt = member + offsets.y + rootReceiver.z * 5u;
    let receiver = lightmap_meta(receiverAt);
    let firstMip = lightmap_meta(receiverAt + 1u).w;
    let point = surface.bake_uv * f32(receiver.x);
    var node = receiver.y; var chartPlusOne = 0u; var rectangle = vec4<u32>(0);
    while (node < receiver.z) {
        let link = lightmap_meta(member + offsets.w + node);
        let bounds = vec4<u32>(link.x & 65535u, link.x >> 16u, link.y & 65535u, link.y >> 16u);
        if (any(point < vec2<f32>(bounds.xy)) || any(point >= vec2<f32>(bounds.zw))) { node = link.z; continue; }
        if (link.w != 0u) { chartPlusOne = link.w; rectangle = bounds; break; }
        node += 1u;
    }
    if (chartPlusOne == 0u || chartPlusOne > counts.z) { return vec4<f32>(0); }
    let coarseAt = member + offsets.z + (chartPlusOne - 1u) * 2u;
    let coarse = lightmap_coefficients(lightmap_meta(coarseAt), lightmap_meta(coarseAt + 1u), scales);
    let footprint = max(length(surface.bake_uv_dx * f32(receiver.x)), length(surface.bake_uv_dy * f32(receiver.x)));
    if (!(footprint >= 0.0) || footprint > f32(receiver.x)) { return vec4<f32>(lightmap_evaluate(coarse, surface.normal), 1); }
    let desired = u32(max(0.0, floor(log2(max(footprint, 1.0)))));
    var dimension = receiver.x; var firstPage = receiver.w;
    for (var level = 0u; level < 13u && dimension >= 2u; level++) {
        if (level < firstMip) { dimension >>= 1u; continue; }
        let edge = min(64u, dimension); let grid = dimension / edge;
        let gridPages = grid * grid;
        if (level < desired) { firstPage += gridPages; dimension >>= 1u; continue; }
        let position = surface.bake_uv * f32(dimension) - vec2<f32>(0.5);
        let cell = vec2<i32>(floor(position)); let factor = i32(1u << level);
        // Guard both taps' entire fine-grid footprints, not only their centres.
        if (any(cell * factor < vec2<i32>(rectangle.xy))
            || any((cell + vec2<i32>(2)) * factor > vec2<i32>(rectangle.zw))) {
            firstPage += gridPages; dimension >>= 1u; continue;
        }
        let fraction = position - vec2<f32>(cell);
        var total = 0.0; var complete = true; var sum: array<vec3<f32>, 4>;
        for (var tap = 0u; tap < 4u; tap++) {
            let corner = vec2<u32>(tap % 2u, tap / 2u); let coordinate = vec2<u32>(cell) + corner;
            let page = firstPage + coordinate.y / edge * grid + coordinate.x / edge;
            let width = textureDimensions(lightmap_mapping).x;
            let slot = textureLoad(lightmap_mapping, vec2<i32>(i32(page % width), i32(page / width)), 0).x;
            if (slot == 0u) { complete = false; break; }
            let local = coordinate % vec2<u32>(edge);
            let packed0 = textureLoad(lightmap_pages, vec2<i32>(i32(local.x * 2u), i32(local.y)), i32(slot - 1u), 0);
            let packed1 = textureLoad(lightmap_pages, vec2<i32>(i32(local.x * 2u + 1u), i32(local.y)), i32(slot - 1u), 0);
            let weight = select(1.0 - fraction.x, fraction.x, corner.x == 1u)
                * select(1.0 - fraction.y, fraction.y, corner.y == 1u);
            if (packed1.z == 0u || weight <= 0.0) { continue; }
            let decoded = lightmap_coefficients(packed0, packed1, scales);
            for (var component = 0u; component < 4u; component++) {
                sum[component] += decoded[component] * weight;
            }
            total += weight;
        }
        if (complete && total > 0.0) {
            for (var component = 0u; component < 4u; component++) { sum[component] /= total; }
            return vec4<f32>(lightmap_evaluate(sum, surface.normal), 1);
        }
        firstPage += gridPages; dimension >>= 1u;
    }
    return vec4<f32>(lightmap_evaluate(coarse, surface.normal), 1);
}
