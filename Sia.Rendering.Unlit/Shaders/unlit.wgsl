struct Instance {
    world: mat4x4<f32>,
    normal: mat4x4<f32>,
    color: vec4<f32>,
};

struct VertexInput {
    @location(0) position: vec3<f32>,
    @location(1) normal: vec3<f32>,
    @location(2) uv: vec2<f32>,
};

struct VertexOutput {
    @builtin(position) position: vec4<f32>,
    @location(0) color: vec4<f32>,
    @location(1) normal: vec3<f32>,
};

@group(0) @binding(0) var<uniform> view_projection: mat4x4<f32>;
@group(0) @binding(1) var<storage, read> instances: array<Instance>;

@vertex
fn vertex(input: VertexInput, @builtin(instance_index) index: u32) -> VertexOutput {
    let instance = instances[index];
    var output: VertexOutput;
    output.position = view_projection * instance.world * vec4<f32>(input.position, 1.0);
    output.color = instance.color;
    output.normal = (instance.normal * vec4<f32>(input.normal, 0.0)).xyz;
    return output;
}

@fragment
fn unlit(input: VertexOutput) -> @location(0) vec4<f32> {
    return input.color;
}

@fragment
fn normals(input: VertexOutput) -> @location(0) vec4<f32> {
    return vec4<f32>(normalize(input.normal) * 0.5 + 0.5, 1.0);
}
