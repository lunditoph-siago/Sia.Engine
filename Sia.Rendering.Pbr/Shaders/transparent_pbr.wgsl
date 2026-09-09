#import pbr::common::{CameraUniform}
#import pbr::scene_lighting::{scene_lighting}

@group(0) @binding(0) var<uniform> camera: CameraUniform;
struct Material {
    transform: mat4x4<f32>, normal_transform: mat4x4<f32>,
    color: vec4<f32>, emission: vec4<f32>, factors: vec4<f32>, flags: vec4<f32>,
}
@group(3) @binding(0) var<uniform> material: Material;
@group(3) @binding(1) var albedo: texture_2d_array<f32>;
@group(3) @binding(2) var albedo_sampler: sampler;
@group(3) @binding(3) var normal_map: texture_2d_array<f32>;
@group(3) @binding(4) var normal_sampler: sampler;
@group(3) @binding(5) var mr_map: texture_2d_array<f32>;
@group(3) @binding(6) var mr_sampler: sampler;
@group(3) @binding(7) var occlusion_map: texture_2d_array<f32>;
@group(3) @binding(8) var occlusion_sampler: sampler;
@group(3) @binding(9) var emission_map: texture_2d_array<f32>;
@group(3) @binding(10) var emission_sampler: sampler;
struct Vertex {
    @location(0) position: vec3<f32>, @location(1) normal: vec3<f32>,
    @location(2) uv: vec2<f32>, @location(3) tangent: vec4<f32>,
}
struct Fragment {
    @builtin(position) clip: vec4<f32>, @location(0) position: vec3<f32>,
    @location(1) normal: vec3<f32>, @location(2) uv: vec2<f32>, @location(3) tangent: vec4<f32>,
}
@vertex fn vertex(input: Vertex) -> Fragment {
    let position = material.transform * vec4<f32>(input.position, 1.0);
    return Fragment(camera.view_proj * position, position.xyz,
        (material.normal_transform * vec4<f32>(input.normal, 0.0)).xyz, input.uv,
        vec4<f32>((material.transform * vec4<f32>(input.tangent.xyz, 0.0)).xyz, input.tangent.w));
}
@fragment fn fragment(input: Fragment, @builtin(front_facing) front: bool) -> @location(0) vec4<f32> {
    let base = textureSample(albedo, albedo_sampler, input.uv, 0) * material.color;
    let sampled = textureSample(normal_map, normal_sampler, input.uv, 0).xyz * 2.0 - 1.0;
    let mr = textureSample(mr_map, mr_sampler, input.uv, 0).gb;
    let ao = mix(1.0, textureSample(occlusion_map, occlusion_sampler, input.uv, 0).r, material.factors.w);
    let emission = textureSample(emission_map, emission_sampler, input.uv, 0).rgb * material.emission.rgb;
    if ((!front && material.flags.x == 0.0) || base.a <= 0.0) { discard; }
    var normal = normalize(input.normal);
    if (material.flags.y != 0.0) {
        let tangent = normalize(input.tangent.xyz - normal * dot(normal, input.tangent.xyz));
        let bitangent = cross(normal, tangent) * input.tangent.w;
        normal = normalize(tangent * sampled.x * material.factors.z + bitangent * sampled.y * material.factors.z + normal * sampled.z);
    }
    if (!front) { normal = -normal; }
    let view = normalize(camera.world_position.xyz - input.position);
    let color = scene_lighting(input.position, normal, view, base.rgb, material.factors.x * mr.y,
        clamp(material.factors.y * mr.x, 0.045, 1.0), emission, ao, input.clip.xy);
    return vec4<f32>(color, base.a);
}
