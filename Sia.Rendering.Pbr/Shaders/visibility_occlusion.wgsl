#define_import_path pbr::occlusion

struct Hierarchy { previous_projection: mat4x4<f32>, size: vec4<u32>, levels: array<vec4<u32>, 32> }
@group(1) @binding(0) var<uniform> hierarchy: Hierarchy;
@group(1) @binding(1) var<storage, read> hzb: array<f32>;

fn occlusion_finite(value: vec4<f32>) -> bool {
    return all((bitcast<vec4<u32>>(value) & vec4<u32>(0x7f800000u)) != vec4<u32>(0x7f800000u));
}

fn occluded(minimum_bounds: vec3<f32>, maximum_bounds: vec3<f32>, matrix: mat4x4<f32>) -> bool {
    var low = vec3<f32>(3.402823466e+38);
    var high = -low;
    for (var corner = 0u; corner < 8u; corner++) {
        let point = select(minimum_bounds, maximum_bounds,
            (vec3<u32>(corner) & vec3<u32>(1u, 2u, 4u)) != vec3<u32>(0u));
        let clip = matrix * vec4<f32>(point, 1.0);
        if (!occlusion_finite(clip) || clip.w <= 0.0 || clip.z <= 0.0) { return false; }
        let ndc = clip.xyz / clip.w;
        if (!occlusion_finite(vec4<f32>(ndc, 1.0))) { return false; }
        low = min(low, ndc);
        high = max(high, ndc);
    }
    let size = vec2<f32>(hierarchy.size.xy);
    let minimum = floor((vec2<f32>(low.x, -high.y) * 0.5 + 0.5) * size) - 2.0;
    let maximum = ceil((vec2<f32>(high.x, -low.y) * 0.5 + 0.5) * size) + 2.0;
    if (any(minimum < vec2<f32>(0.0)) || any(maximum >= size)) { return false; }
    var level = 0u;
    var factor = hierarchy.size.z;
    var first = vec2<u32>(minimum) / factor;
    var last = vec2<u32>(maximum) / factor;
    while (any(last - first > vec2<u32>(1u)) && level < 31u) {
        if (all(hierarchy.levels[level].xy == vec2<u32>(1u))) { break; }
        level++;
        factor *= 2u;
        first = vec2<u32>(minimum) / factor;
        last = vec2<u32>(maximum) / factor;
    }
    let mip = hierarchy.levels[level];
    var farthest = 0.0;
    for (var y = first.y; y <= last.y; y++) {
        for (var x = first.x; x <= last.x; x++) {
            farthest = max(farthest, hzb[mip.z + y * mip.x + x]);
        }
    }
    return low.z > farthest + 1e-5;
}
