#import pbr::visibility
#import pbr::materials
#import pbr::pbr::{direct_lighting}

@group(1) @binding(0) var visibility: texture_2d<u32>;
@group(1) @binding(1) var hdr: texture_storage_2d<rgba16float, write>;
@group(1) @binding(2) var albedo: texture_2d_array<f32>;
@group(1) @binding(3) var albedo_sampler: sampler;
@group(1) @binding(4) var normal_map: texture_2d_array<f32>;
@group(1) @binding(5) var normal_sampler: sampler;
@group(1) @binding(6) var metallic_roughness_map: texture_2d_array<f32>;
@group(1) @binding(7) var metallic_roughness_sampler: sampler;
@group(1) @binding(8) var occlusion_map: texture_2d_array<f32>;
@group(1) @binding(9) var occlusion_sampler: sampler;
@group(1) @binding(10) var emissive_map: texture_2d_array<f32>;
@group(1) @binding(11) var emissive_sampler: sampler;
@group(1) @binding(12) var<uniform> material_batch: vec4<u32>;
@group(1) @binding(13) var base_roughness: texture_storage_2d<rgba16float, write>;
@group(1) @binding(14) var normal_metallic: texture_storage_2d<rgba16float, write>;
@group(1) @binding(15) var emissive_occlusion: texture_storage_2d<rgba16float, write>;
@group(1) @binding(16) var<storage, read> material_tiles: array<u32>;
@group(1) @binding(17) var<storage, read> material_table: array<VisibilityMaterial>;

fn clear_surface(pixel: vec2<i32>) {
    textureStore(base_roughness, pixel, vec4<f32>(0.0));
    textureStore(normal_metallic, pixel, vec4<f32>(0.0));
    textureStore(emissive_occlusion, pixel, vec4<f32>(0.0));
}

fn safe_normalize(value: vec3<f32>) -> vec3<f32> {
    return value * inverseSqrt(max(dot(value, value), 1e-20));
}

fn structure_color(index: u32) -> vec3<f32> {
    var hash = index + 1u;
    hash = (hash ^ (hash >> 16u)) * 0x7feb352du;
    hash = (hash ^ (hash >> 15u)) * 0x846ca68bu;
    hash = hash ^ (hash >> 16u);
    return vec3<f32>(0.2) + vec3<f32>(f32(hash & 255u), f32((hash >> 8u) & 255u),
        f32((hash >> 16u) & 255u)) * (0.8 / 255.0);
}

fn resolve_pixel(group: vec3<u32>, local: vec3<u32>, mode: u32, scene_lighting: bool) {
    let size = visibility_camera.size_counts.xy;
    let dimensions = (size + 7u) / 8u;
    let base = material_batch.x * (dimensions.x * dimensions.y + 1u);
    let ordinal = group.x + group.y * 65535u;
    if (ordinal >= material_tiles[base]) { return; }
    let tile = material_tiles[base + 1u + ordinal];
    let thread = vec3<u32>(vec2<u32>(tile % dimensions.x, tile / dimensions.x) * 8u + local.xy, 0u);
    if (any(thread.xy >= size)) { return; }
    let pixel = vec2<i32>(thread.xy);
    let id = textureLoad(visibility, pixel, 0).x;
    let triangle_count = visibility_camera.size_counts.z;
    if (id == 0u || triangle_count == 0u) {
        if (material_batch.x != 0u) { return; }
        clear_surface(pixel);
        if (mode != 0u || !scene_lighting) { textureStore(hdr, pixel, vec4<f32>(0.015, 0.02, 0.03, 1.0)); }
        return;
    }
    if (id > triangle_count) {
        if (material_batch.x != 0u) { return; }
        clear_surface(pixel);
        if (mode != 0u || !scene_lighting) { textureStore(hdr, pixel, vec4<f32>(1.0, 0.0, 1.0, 1.0)); }
        return;
    }
    let work = visibility_triangle_work(id - 1u);
    let triangle = work.x;
    let instance = visibility_instances[work.y];
    let material_parameters = material_table[u32(instance.material.z)];
    if (material_parameters.indices.y != material_batch.x) { return; }
    let a = visibility_vertex(triangle, 0u);
    let b = visibility_vertex(triangle, 1u);
    let c = visibility_vertex(triangle, 2u);
    let world_a = instance.transform * vec4<f32>(a.position.xyz, 1.0);
    let world_b = instance.transform * vec4<f32>(b.position.xyz, 1.0);
    let world_c = instance.transform * vec4<f32>(c.position.xyz, 1.0);
    let clip_a = visibility_camera.view_projection * world_a;
    let clip_b = visibility_camera.view_projection * world_b;
    let clip_c = visibility_camera.view_projection * world_c;

    let cofactor_a = cross(clip_b.xyw, clip_c.xyw);
    let cofactor_b = cross(clip_c.xyw, clip_a.xyw);
    let cofactor_c = cross(clip_a.xyw, clip_b.xyw);
    let ndc = (vec2<f32>(thread.xy) + 0.5) / vec2<f32>(size) * vec2<f32>(2.0, -2.0)
        + vec2<f32>(-1.0, 1.0);
    let q = vec3<f32>(ndc, 1.0);
    let weights = vec3<f32>(dot(cofactor_a, q), dot(cofactor_b, q), dot(cofactor_c, q));
    let denominator = dot(weights, vec3<f32>(1.0));
    if (abs(denominator) < 1e-20) {
        clear_surface(pixel);
        if (mode != 0u || !scene_lighting) { textureStore(hdr, pixel, vec4<f32>(0.0, 0.0, 0.0, 1.0)); }
        return;
    }
    let bary = weights / denominator;
    let dx = vec3<f32>(cofactor_a.x, cofactor_b.x, cofactor_c.x) * (2.0 / f32(size.x));
    let dy = vec3<f32>(cofactor_a.y, cofactor_b.y, cofactor_c.y) * (-2.0 / f32(size.y));
    if (mode == 4u) {
        clear_surface(pixel);
        let gradient_x = (dx - bary * dot(dx, vec3<f32>(1.0))) / denominator;
        let gradient_y = (dy - bary * dot(dy, vec3<f32>(1.0))) / denominator;
        let edge_distance = abs(bary) / max(sqrt(gradient_x * gradient_x + gradient_y * gradient_y), vec3<f32>(1e-20));
        let interior = smoothstep(0.35, 1.1, min(edge_distance.x, min(edge_distance.y, edge_distance.z)));
        let normal = safe_normalize(cross(world_b.xyz - world_a.xyz, world_c.xyz - world_a.xyz));
        let facing = 0.65 + 0.35 * abs(dot(normal, safe_normalize(visibility_camera.eye.xyz - world_a.xyz)));
        let color = structure_color(triangle) * facing;
        textureStore(hdr, pixel, vec4<f32>(mix(color * 0.2, color, interior), 1.0));
        return;
    }
    let uv = a.uv.xy * bary.x + b.uv.xy * bary.y + c.uv.xy * bary.z;
    let uv_dx = (a.uv.xy * dx.x + b.uv.xy * dx.y + c.uv.xy * dx.z
        - uv * dot(dx, vec3<f32>(1.0))) / denominator;
    let uv_dy = (a.uv.xy * dy.x + b.uv.xy * dy.y + c.uv.xy * dy.z
        - uv * dot(dy, vec3<f32>(1.0))) / denominator;
    let base_color = textureSampleGrad(albedo, albedo_sampler, uv, i32(material_parameters.layers.x), uv_dx, uv_dy).rgb
        * instance.color.rgb * material_parameters.color_metallic.rgb;
    let local_normal = a.normal.xyz * bary.x + b.normal.xyz * bary.y + c.normal.xyz * bary.z;
    var normal = safe_normalize((instance.normal_transform * vec4<f32>(local_normal, 0.0)).xyz);
    if (material_parameters.texture_factors.z != 0.0) {
        let local_tangent = a.tangent * bary.x + b.tangent * bary.y + c.tangent * bary.z;
        let transformed = (instance.transform * vec4<f32>(local_tangent.xyz, 0.0)).xyz;
        var tangent = transformed - normal * dot(normal, transformed);
        if (dot(tangent, tangent) < 1e-12) {
            let axis = select(vec3<f32>(0.0, 1.0, 0.0), vec3<f32>(1.0, 0.0, 0.0), abs(normal.x) < 0.9);
            tangent = cross(axis, normal);
        }
        tangent = safe_normalize(tangent);
        let bitangent = cross(normal, tangent) * select(1.0, -1.0, local_tangent.w < 0.0);
        var sampled_normal = textureSampleGrad(normal_map, normal_sampler, uv, i32(material_parameters.layers.y), uv_dx, uv_dy).rgb * 2.0 - 1.0;
        sampled_normal = vec3<f32>(sampled_normal.xy * material_parameters.texture_factors.x, sampled_normal.z);
        normal = safe_normalize(tangent * sampled_normal.x + bitangent * sampled_normal.y + normal * sampled_normal.z);
    }
    let mr = textureSampleGrad(metallic_roughness_map, metallic_roughness_sampler, uv, i32(material_parameters.layers.z), uv_dx, uv_dy).gb;
    let metallic = clamp(instance.material.x * material_parameters.color_metallic.w * mr.y, 0.0, 1.0);
    let roughness = clamp(instance.material.y * material_parameters.emissive_roughness.w * mr.x, 0.045, 1.0);
    var occlusion = 1.0;
    if (material_parameters.texture_factors.y != 0.0) {
        occlusion = mix(1.0, textureSampleGrad(occlusion_map, occlusion_sampler, uv, i32(material_parameters.layers.w), uv_dx, uv_dy).r,
            material_parameters.texture_factors.y);
    }
    var emissive = vec3<f32>(0.0);
    if (any(material_parameters.emissive_roughness.rgb != vec3<f32>(0.0)) && any(instance.emissive.rgb != vec3<f32>(0.0))) {
        emissive = textureSampleGrad(emissive_map, emissive_sampler, uv, i32(material_parameters.indices.x), uv_dx, uv_dy).rgb
            * material_parameters.emissive_roughness.rgb * instance.emissive.rgb;
    }
    textureStore(base_roughness, pixel, vec4<f32>(base_color, roughness));
    textureStore(normal_metallic, pixel, vec4<f32>(normal, metallic));
    textureStore(emissive_occlusion, pixel, vec4<f32>(emissive, occlusion));
    if (mode == 0u && scene_lighting) { return; }
    let position = world_a.xyz * bary.x + world_b.xyz * bary.y + world_c.xyz * bary.z;
    var color = base_color;
    if (mode == 0u && !scene_lighting) {
        color = direct_lighting(normal, safe_normalize(visibility_camera.eye.xyz - position),
            visibility_camera.light_direction.xyz, visibility_camera.light_radiance.xyz,
            base_color, metallic, roughness) + emissive;
    } else if (mode == 1u) {
        color = normal * 0.5 + 0.5;
    } else if (mode == 2u) {
        color = vec3<f32>(fract(uv), 0.0);
    }
    textureStore(hdr, pixel, vec4<f32>(color, 1.0));
}

@compute @workgroup_size(8, 8)
fn resolve(@builtin(workgroup_id) group: vec3<u32>, @builtin(local_invocation_id) local: vec3<u32>) {
    resolve_pixel(group, local, visibility_camera.size_counts.w & 255u, (visibility_camera.size_counts.w & 256u) != 0u);
}

@compute @workgroup_size(8, 8)
fn resolve_surface(@builtin(workgroup_id) group: vec3<u32>, @builtin(local_invocation_id) local: vec3<u32>) {
    resolve_pixel(group, local, 0u, true);
}
