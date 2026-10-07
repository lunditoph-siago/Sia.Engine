#define_import_path rendering/Lighting/scene_trace

// Caller supplies trace_record(index, dynamic); zero visit budget traverses the full BVH.
struct TraceHit {
    distance: f32,
    triangle: u32,
    front: bool,
    dynamic: bool,
    complete: bool,
}

fn trace_bvh(origin: vec3<f32>, direction: vec3<f32>, maximum: f32, dynamic: bool, maximum_visits: u32) -> TraceHit {
    var hit = TraceHit(maximum, 0xffffffffu, true, dynamic, true);
    var node = 0u;
    let nodes = u32(trace_record(0u, dynamic).x);
    let triangle_start = u32(trace_record(0u, dynamic).y);
    // A tiny signed substitute keeps slab math finite for axis-parallel rays.
    let safe = select(direction, select(vec3<f32>(-1e-20), vec3<f32>(1e-20), direction >= vec3<f32>(0)), abs(direction) < vec3<f32>(1e-20));
    let inverse = 1.0 / safe;
    var visits = 0u;
    loop {
        if (node >= nodes) { break; }
        if (maximum_visits != 0u && visits >= maximum_visits) { hit.complete = false; break; }
        visits++;
        let lo = trace_record(2u + node*3u, dynamic);
        let hi = trace_record(3u + node*3u, dynamic);
        let a = (lo.xyz-origin)*inverse; let b = (hi.xyz-origin)*inverse;
        let near = min(a,b); let far = max(a,b);
        if (max(max(near.x,near.y),max(near.z,0.001)) > min(min(far.x,far.y),min(far.z,hit.distance))) {
            node = u32(lo.w); continue;
        }
        let count = u32(trace_record(4u + node*3u, dynamic).x);
        for (var i=0u; i<count; i++) {
            let triangle = u32(hi.w)+i;
            let indices = vec4<u32>(trace_record(triangle_start + triangle, dynamic));
            let vertex_start = u32(trace_record(1u, dynamic).x);
            let position = trace_record(vertex_start+indices.x, dynamic).xyz;
            let edge1 = trace_record(vertex_start+indices.y, dynamic).xyz-position;
            let edge2 = trace_record(vertex_start+indices.z, dynamic).xyz-position;
            let p = cross(direction,edge2); let determinant = dot(edge1,p);
            if (abs(determinant)<1e-8) { continue; }
            let t = origin-position;
            let u = dot(t,p)/determinant;
            let q = cross(t,edge1); let v = dot(direction,q)/determinant;
            let distance = dot(edge2,q)/determinant;
            // Trace both sides for conservative occlusion; backfaces mark invalid probe positions.
            if (u>=0.0 && v>=0.0 && u+v<=1.0 && distance>0.001 && distance<hit.distance) {
                hit = TraceHit(distance,triangle,determinant>0.0,dynamic,true);
            }
        }
        node++;
    }
    return hit;
}

fn trace_scene(origin: vec3<f32>, direction: vec3<f32>, maximum: f32, actors: bool, maximum_visits: u32) -> TraceHit {
    var hit = trace_bvh(origin, direction, maximum, false, maximum_visits);
#if DYNAMIC_TRACE
    if (actors) {
        let actor = trace_bvh(origin, direction, hit.distance, true, maximum_visits);
        let complete = hit.complete && actor.complete;
        if (actor.triangle != 0xffffffffu) { hit = actor; }
        hit.complete = complete;
    }
#endif
    return hit;
}
