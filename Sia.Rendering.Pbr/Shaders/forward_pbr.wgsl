#import pbr::common::{CameraUniform, InstanceData, VertexInput}
#import pbr::scene_lighting::{scene_lighting}

@group(0) @binding(0) var<uniform> camera: CameraUniform;
@group(0) @binding(1) var<storage, read> instances: array<InstanceData>;

struct VertexOutput {
    @builtin(position) clip_position: vec4<f32>,
    @location(0) world_position: vec3<f32>,
    @location(1) world_normal: vec3<f32>,
    @location(2) base_color: vec4<f32>,
    @location(3) material_params: vec4<f32>,
    @location(4) emissive: vec4<f32>,
};

@vertex
fn vertex(input: VertexInput, @builtin(instance_index) instance_index: u32) -> VertexOutput {
    let instance = instances[instance_index];
    let world_position = instance.world_matrix * vec4<f32>(input.position, 1.0);
    let world_normal = normalize((instance.normal_matrix * vec4<f32>(input.normal, 0.0)).xyz);

    var output: VertexOutput;
    output.clip_position = camera.view_proj * world_position;
    output.world_position = world_position.xyz;
    output.world_normal = world_normal;
    output.base_color = instance.base_color;
    output.material_params = instance.material_params;
    output.emissive = instance.emissive;
    return output;
}

@fragment
fn fragment(input: VertexOutput) -> @location(0) vec4<f32> {
    let normal = normalize(input.world_normal);
    let view_dir = normalize(camera.world_position.xyz - input.world_position);
    let base_color = input.base_color.rgb;
    let metallic = input.material_params.x;
    let roughness = clamp(input.material_params.y, 0.045, 1.0);

    let color = scene_lighting(input.world_position, normal, view_dir, base_color, metallic, roughness,
        input.emissive.rgb * input.emissive.a, 1.0, input.clip_position.xy);
    return vec4<f32>(color, input.base_color.a);
}
