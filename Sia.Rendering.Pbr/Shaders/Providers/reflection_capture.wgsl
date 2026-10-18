#define_import_path pbr/Providers/reflection_capture

#import pbr/bindings

// Returns face and normalized image coordinates matching independent environment payload order.
fn capture_cube_coordinates(direction: vec3<f32>) -> vec3<f32> {
    let magnitude = abs(direction); var face = 0.0; var uv = vec2<f32>(0);
    if (magnitude.x >= magnitude.y && magnitude.x >= magnitude.z) {
        if (direction.x >= 0.0) { face = 0.0; uv = vec2<f32>(-direction.z, -direction.y) / magnitude.x; }
        else { face = 1.0; uv = vec2<f32>(direction.z, -direction.y) / magnitude.x; }
    }
    else if (magnitude.y >= magnitude.z) {
        if (direction.y >= 0.0) { face = 2.0; uv = vec2<f32>(direction.x, direction.z) / magnitude.y; }
        else { face = 3.0; uv = vec2<f32>(direction.x, -direction.z) / magnitude.y; }
    }
    else {
        if (direction.z >= 0.0) { face = 4.0; uv = vec2<f32>(direction.x, -direction.y) / magnitude.z; }
        else { face = 5.0; uv = vec2<f32>(-direction.x, -direction.y) / magnitude.z; }
    }
    return vec3<f32>(uv * 0.5 + 0.5, face);
}

fn capture_cube_direction(face: i32, uv: vec2<f32>) -> vec3<f32> {
    switch face {
        case 0: { return vec3<f32>(1, -uv.y, -uv.x); }
        case 1: { return vec3<f32>(-1, -uv.y, uv.x); }
        case 2: { return vec3<f32>(uv.x, 1, uv.y); }
        case 3: { return vec3<f32>(uv.x, -1, -uv.y); }
        case 4: { return vec3<f32>(uv.x, -uv.y, 1); }
        default: { return vec3<f32>(-uv.x, -uv.y, -1); }
    }
}

fn capture_texel(face: i32, pixel: vec2<i32>, edge: i32, base_layer: i32, mip: i32) -> vec3<f32> {
    var coordinate = pixel; var selected_face = face;
    // Fold an out-of-face bilinear tap into its adjacent face; never clamp a seam to one face's edge.
    if (any(pixel < vec2<i32>(0)) || any(pixel >= vec2<i32>(edge))) {
        let direction = capture_cube_direction(face, (vec2<f32>(pixel) + 0.5) / f32(edge) * 2.0 - 1.0);
        let folded = capture_cube_coordinates(direction);
        coordinate = clamp(vec2<i32>(floor(folded.xy * f32(edge))), vec2<i32>(0), vec2<i32>(edge - 1));
        selected_face = i32(folded.z);
    }
    return textureLoad(capture_environment, coordinate, base_layer + selected_face, mip).rgb;
}

fn capture_mip(direction: vec3<f32>, base_layer: i32, mip: i32) -> vec3<f32> {
    let projected = capture_cube_coordinates(direction);
    let edge = i32(textureDimensions(capture_environment, mip).x);
    let pixel = projected.xy * f32(edge) - 0.5; let first = vec2<i32>(floor(pixel)); let blend = fract(pixel);
    let face = i32(projected.z);
    let low = mix(capture_texel(face, first, edge, base_layer, mip), capture_texel(face, first + vec2<i32>(1, 0), edge, base_layer, mip), blend.x);
    let high = mix(capture_texel(face, first + vec2<i32>(0, 1), edge, base_layer, mip), capture_texel(face, first + vec2<i32>(1, 1), edge, base_layer, mip), blend.x);
    return mix(low, high, blend.y);
}

fn capture_specular(position: vec3<f32>, direction: vec3<f32>, roughness: f32, sky: vec3<f32>) -> vec3<f32> {
    var selected = 0u; var influence = 0.0;
    // UUID-sorted regions: choose the strongest finite influence, with stable ties and bounded sample cost.
    for (var region = 0u; region < min(u32(capture_regions[0].x), #{MAXIMUM_CAPTURE_REGIONS}u); region++) {
        let center = capture_regions[1u + region * 3u].xyz;
        let minimum = capture_regions[2u + region * 3u].xyz; let maximum = capture_regions[3u + region * 3u].xyz;
        if (any(position < minimum) || any(position > maximum)) { continue; }
        let edge = min((position - minimum) / (center - minimum), (maximum - position) / (maximum - center));
        let distance = clamp(min(edge.x, min(edge.y, edge.z)), 0.0, 1.0);
        let weight = distance * distance * (3.0 - 2.0 * distance);
        if (weight > influence) { selected = region; influence = weight; }
    }
    if (influence <= 0.0) { return sky; }
    let center = capture_regions[1u + selected * 3u];
    let minimum = capture_regions[2u + selected * 3u].xyz; let maximum = capture_regions[3u + selected * 3u].xyz;
    // Solve in a uniformly scaled local box, so large finite world extents cannot overflow the ray exit or normalization.
    let extent = maximum - minimum; let box_scale = max(extent.x, max(extent.y, extent.z));
    let local_position = (position - center.xyz) / box_scale;
    let local_minimum = (minimum - center.xyz) / box_scale; let local_maximum = (maximum - center.xyz) / box_scale;
    var exit = 3.402823e38;
    for (var axis = 0u; axis < 3u; axis++) {
        if (direction[axis] > 0.0) { exit = min(exit, (local_maximum[axis] - local_position[axis]) / direction[axis]); }
        else if (direction[axis] < 0.0) { exit = min(exit, (local_minimum[axis] - local_position[axis]) / direction[axis]); }
    }
    var projected = local_position + direction * exit;
    let scale = max(abs(projected.x), max(abs(projected.y), abs(projected.z)));
    if (scale <= 0.0) { return sky; }
    projected = normalize(projected / scale);
    let level = clamp(roughness, 0.0, 1.0) * f32(textureNumLevels(capture_environment) - 1u);
    let first = i32(floor(level)); let last = min(first + 1, i32(textureNumLevels(capture_environment)) - 1);
    let radiance = mix(capture_mip(projected, i32(center.w), first), capture_mip(projected, i32(center.w), last), fract(level));
    return mix(sky, radiance, influence);
}
