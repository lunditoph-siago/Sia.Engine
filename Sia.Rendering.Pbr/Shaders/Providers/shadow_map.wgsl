#define_import_path pbr/Providers/shadow_map

#import pbr/bindings
#import rendering/Lighting/shadow_map

fn shadow_visibility(layer: u32, position: vec3<f32>, geometry_normal: vec3<f32>) -> f32 {
    return rendering_shadow_visibility_gather(shadow_atlas, depth_sampler, shadow_gather_sampler, layer, shadow_matrix(layer), position, geometry_normal, 0.0015);
}

// Cascades are orthographic. Project a bounded world-space receiver offset
// toward the light instead of allowing cascade depth range to scale the bias.
fn directional_shadow_depth_bias(matrix: mat4x4<f32>, light_direction: vec3<f32>,
    geometry_normal: vec3<f32>, world_bias: vec2<f32>) -> f32 {
    let light_scale = max(max(abs(light_direction.x), abs(light_direction.y)), abs(light_direction.z));
    if (!(light_scale > 0.0) || !(light_scale < 1e30) || !(abs(matrix[3].w) > 1e-20)) { return 0.0; }
    let direction = normalize(light_direction / light_scale);
    var slope = 0.0;
    let normal_scale = max(max(abs(geometry_normal.x), abs(geometry_normal.y)), abs(geometry_normal.z));
    if (normal_scale > 0.0 && all(abs(geometry_normal) < vec3<f32>(1e30))) {
        let normal = normalize(geometry_normal / normal_scale);
        let cosine = clamp(abs(dot(normal, direction)), 0.0, 1.0);
        slope = sqrt(max(0.0, 1.0 - cosine * cosine));
    }
    let gradient = vec3<f32>(matrix[0].z, matrix[1].z, matrix[2].z) / matrix[3].w;
    return (world_bias.x + world_bias.y * slope) * abs(dot(gradient, direction));
}

fn directional_shadow_visibility(layer: u32, position: vec3<f32>, geometry_normal: vec3<f32>,
    light_direction: vec3<f32>, world_bias: vec2<f32>) -> f32 {
    let matrix = shadow_matrix(layer);
    let bias = directional_shadow_depth_bias(matrix, light_direction, geometry_normal, world_bias);
    return rendering_shadow_visibility_gather(shadow_atlas, depth_sampler, shadow_gather_sampler,
        layer, matrix, position, geometry_normal, bias);
}

fn point_shadow_face(light_to_surface: vec3<f32>) -> u32 {
    let magnitude = abs(light_to_surface);
    if (magnitude.x >= magnitude.y && magnitude.x >= magnitude.z) {
        return select(1u, 0u, light_to_surface.x >= 0.0);
    }
    if (magnitude.y >= magnitude.z) { return select(3u, 2u, light_to_surface.y >= 0.0); }
    return select(5u, 4u, light_to_surface.z >= 0.0);
}

// Perspective z = A - B / axial_distance. Convert a bounded world-space
// receiver offset along the light ray to the exact corresponding depth delta.
// A fixed normalized-depth bias would detach distant point-light shadows by
// metres when near=.05; the world-space policy remains 1..3mm by default.
fn point_shadow_depth_bias(light_to_surface: vec3<f32>, geometry_normal: vec3<f32>,
    depth_coefficient: f32, world_bias: vec2<f32>) -> f32 {
    let magnitude = abs(light_to_surface);
    let axial_distance = max(max(magnitude.x, magnitude.y), magnitude.z);
    let distance = length(light_to_surface);
    if (!(axial_distance > 0.0) || !(distance > 0.0) || !(distance < 1e30)) { return 0.0; }
    var slope = 0.0;
    let normal_scale = max(max(abs(geometry_normal.x), abs(geometry_normal.y)), abs(geometry_normal.z));
    if (normal_scale > 0.0 && all(abs(geometry_normal) < vec3<f32>(1e30))) {
        let n = normalize(geometry_normal / normal_scale);
        let cosine = clamp(abs(dot(n, light_to_surface / distance)), 0.0, 1.0);
        slope = sqrt(max(0.0, 1.0 - cosine * cosine));
    }
    let fraction = min((world_bias.x + world_bias.y * slope) / distance, 0.5);
    return depth_coefficient * fraction / (axial_distance * (1.0 - fraction));
}

fn point_shadow_visibility(layer: u32, position: vec3<f32>, geometry_normal: vec3<f32>,
    light_position: vec3<f32>, depth_coefficient: f32, world_bias: vec2<f32>) -> f32 {
    let bias = point_shadow_depth_bias(position - light_position, geometry_normal, depth_coefficient, world_bias);
    return rendering_shadow_visibility_gather(shadow_atlas, depth_sampler, shadow_gather_sampler, layer, shadow_matrix(layer), position, geometry_normal, bias);
}
