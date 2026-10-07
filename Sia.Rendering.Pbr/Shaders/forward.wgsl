#define_import_path pbr/forward

#import pbr/lighting

struct ForwardVertex {
    @builtin(position) @invariant position: vec4<f32>,
    @location(0) world: vec3<f32>,
    @location(1) normal: vec3<f32>,
    @location(2) tangent: vec4<f32>,
    @location(3) uv: vec2<f32>,
    @location(4) @interpolate(flat, either) material: u32,
    @location(5) lightmap: vec3<f32>,
}

@vertex
fn prepass_vertex(
#if COMPACT_VERTICES
    @location(0) packed: vec4<u32>,
#else
    @location(0) p: vec4<f32>,
#endif
    @builtin(instance_index) instance_id: u32,
) -> @builtin(position) @invariant vec4<f32> {
#if COMPACT_VERTICES
    let p = bitcast<vec4<f32>>(packed);
#endif
    var world = vec4<f32>(p.xyz, 1.0);
#if LOCAL_INSTANCES
    world = instances[instance_id].transform * world;
#endif
    return frame.vp * world;
}

@vertex
fn forward_vertex(
#if COMPACT_VERTICES
    @location(0) packed_p: vec4<u32>,
    @location(1) packed_n: vec4<u32>,
#else
    @location(0) p: vec4<f32>,
    @location(1) n: vec4<f32>,
    @location(2) t: vec4<f32>,
#endif
    @builtin(instance_index) instance_id: u32,
    @builtin(vertex_index) vertex_id: u32,
) -> ForwardVertex {
#if COMPACT_VERTICES
    let vertex = compact_vertex(packed_p, packed_n, vertex_lightmap(vertex_id));
    let p = vec4<f32>(vertex.position, vertex.normal.x);
    let n = vec4<f32>(vertex.normal.yz, vertex.uv);
    let t = vertex.tangent;
#endif
    var world = vec4<f32>(p.xyz, 1.0);
#if LOCAL_INSTANCES
    let instance = instances[instance_id];
    world = instance.transform * world;
    return ForwardVertex(frame.vp * world, world.xyz,
        (instance.normal_transform * vec4<f32>(p.w, n.xy, 0.0)).xyz,
        vec4<f32>((instance.transform * vec4<f32>(t.xyz, 0.0)).xyz, sign(t.w)), n.zw, instance.material.x, vertex_lightmap(vertex_id));
#else
#if COMPACT_VERTICES
    let material = vertex_owner(packed_n.w);
#else
    let material = u32(abs(t.w)) - 1u;
#endif
    return ForwardVertex(frame.vp * world, world.xyz,
        vec3<f32>(p.w, n.xy),
        t, n.zw, material, vertex_lightmap(vertex_id));
#endif
}

@fragment
fn forward_fragment(
    input: ForwardVertex,
    @builtin(front_facing) front: bool,
) -> @location(0) vec4<f32> {
    var surface = sample_material(materials[input.material], input.world, input.normal,
        input.tangent, input.uv, dpdx(input.uv), dpdy(input.uv), front);
    surface.lightmap = input.lightmap;
    surface.lightmap_dx = dpdx(input.lightmap.xy);
    surface.lightmap_dy = dpdy(input.lightmap.xy);
    return vec4<f32>(min(shade(surface, input.position.xy), vec3<f32>(65504)), 1.0);
}
