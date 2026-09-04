struct CameraUniforms {
  view_projection: mat4x4<f32>,
};

@group(0) @binding(0)
var<uniform> camera: CameraUniforms;

struct VertexInput {
  @location(0) position: vec3<f32>,
  @location(1) normal: vec3<f32>,
  @location(2) color: vec4<f32>,
};

struct VertexOutput {
  @builtin(position) clip_position: vec4<f32>,
  @location(0) normal: vec3<f32>,
  @location(1) color: vec4<f32>,
};

@vertex
fn vertex(input: VertexInput) -> VertexOutput {
  var output: VertexOutput;
  output.clip_position = camera.view_projection * vec4<f32>(input.position, 1.0);
  output.normal = input.normal;
  output.color = input.color;
  return output;
}

@fragment
fn fragment(input: VertexOutput) -> @location(0) vec4<f32> {
  let light_direction = normalize(vec3<f32>(0.45, 0.82, 0.34));
  let lighting = 0.25 + 0.75 * abs(dot(normalize(input.normal), light_direction));
  return vec4<f32>(input.color.rgb * lighting, input.color.a);
}
