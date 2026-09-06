#import pbr::visibility
#import pbr::pbr::{direct_lighting}

@group(1) @binding(0) var visibility: texture_2d<u32>;
@group(1) @binding(1) var hdr: texture_storage_2d<rgba16float, write>;
@group(1) @binding(2) var albedo: texture_2d<f32>;
@group(1) @binding(3) var albedo_sampler: sampler;

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

@compute @workgroup_size(8, 8)
fn resolve(@builtin(global_invocation_id) thread: vec3<u32>) {
    let size = visibility_camera.size_counts.xy;
    if (any(thread.xy >= size)) { return; }
    let pixel = vec2<i32>(thread.xy);
    let id = textureLoad(visibility, pixel, 0).x;
    let triangle_count = visibility_camera.size_counts.z;
    if (id == 0u || triangle_count == 0u) {
        textureStore(hdr, pixel, vec4<f32>(0.015, 0.02, 0.03, 1.0));
        return;
    }
    if (id > triangle_count) {
        textureStore(hdr, pixel, vec4<f32>(1.0, 0.0, 1.0, 1.0));
        return;
    }
    let work = visibility_work[id - 1u];
    let triangle = work.x;
    let instance = visibility_instances[work.y];
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
        textureStore(hdr, pixel, vec4<f32>(0.0, 0.0, 0.0, 1.0));
        return;
    }
    let bary = weights / denominator;
    let dx = vec3<f32>(cofactor_a.x, cofactor_b.x, cofactor_c.x) * (2.0 / f32(size.x));
    let dy = vec3<f32>(cofactor_a.y, cofactor_b.y, cofactor_c.y) * (-2.0 / f32(size.y));
    let mode = visibility_camera.size_counts.w;
    if (mode == 4u) {
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
    let base_color = textureSampleGrad(albedo, albedo_sampler, uv, uv_dx, uv_dy).rgb * instance.color.rgb;
    let local_normal = a.normal.xyz * bary.x + b.normal.xyz * bary.y + c.normal.xyz * bary.z;
    let normal = safe_normalize((instance.normal_transform * vec4<f32>(local_normal, 0.0)).xyz);
    let position = world_a.xyz * bary.x + world_b.xyz * bary.y + world_c.xyz * bary.z;
    var color = base_color;
    if (mode == 0u) {
        color = direct_lighting(normal, safe_normalize(visibility_camera.eye.xyz - position),
            visibility_camera.light_direction.xyz, visibility_camera.light_radiance.xyz,
            base_color, instance.material.x, instance.material.y) + instance.emissive.rgb;
    } else if (mode == 1u) {
        color = normal * 0.5 + 0.5;
    } else if (mode == 2u) {
        color = vec3<f32>(fract(uv), 0.0);
    }
    textureStore(hdr, pixel, vec4<f32>(color, 1.0));
}
