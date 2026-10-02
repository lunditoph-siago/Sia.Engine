#define_import_path pbr/forward

#import pbr/lighting

struct ForwardVertex {
    @builtin(position) @invariant position: vec4<f32>,
    @location(0) world: vec3<f32>,
    @location(1) normal: vec3<f32>,
    @location(2) tangent: vec4<f32>,
    @location(3) uv: vec2<f32>,
    @location(4) @interpolate(flat, either) material: u32,
}

@vertex
fn prepass_vertex(
    @location(0) p: vec4<f32>,
    @builtin(instance_index) instance_id: u32,
) -> @builtin(position) @invariant vec4<f32> {
    let world = vec4<f32>(p.xyz, 1.0);
    return frame.vp * world;
}

@vertex
fn forward_vertex(
    @location(0) p: vec4<f32>,
    @location(1) n: vec4<f32>,
    @location(2) t: vec4<f32>,
    @builtin(instance_index) instance_id: u32,
) -> ForwardVertex {
    let world = vec4<f32>(p.xyz, 1.0);
    return ForwardVertex(frame.vp * world, world.xyz,
        vec3<f32>(p.w, n.xy),
        t, n.zw, u32(abs(t.w)) - 1u);
}

@fragment
fn forward_fragment(
    input: ForwardVertex,
    @builtin(front_facing) front: bool,
) -> @location(0) vec4<f32> {
    let surface = sample_material(materials[input.material], input.world, input.normal,
        input.tangent, input.uv, dpdx(input.uv), dpdy(input.uv), front);
    return vec4<f32>(min(shade(surface, input.position.xy), vec3<f32>(65504)), 1.0);
}
