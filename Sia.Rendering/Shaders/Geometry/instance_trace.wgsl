#define_import_path rendering/Geometry/instance_trace

// Caller declares read-only storage arrays instance_trace_geometry and instance_trace_instances (vec4<u32>).
// Inputs must be complete PbrBvhTrace GPU packets; header/digest checks guard pair identity, not arbitrary corruption.
struct InstanceTraceHit {
    parameter: f32,
    instance: u32,
    primitive: u32,
    slot: u32,
    material: u32,
    barycentric: vec3<f32>,
    front: bool,
    normal: vec3<f32>,
}

fn instance_trace_word(word: u32) -> u32 {
    return instance_trace_geometry[word / 4u][word % 4u];
}

fn instance_trace_box(lo: vec3<f32>, hi: vec3<f32>, origin: vec3<f32>, direction: vec3<f32>, lower: f32, upper: f32) -> bool {
    var minimum = lower; var maximum = upper;
    for (var axis = 0u; axis < 3u; axis++) {
        if (direction[axis] == 0.0) {
            if (origin[axis] < lo[axis] || origin[axis] > hi[axis]) { return false; }
        } else {
            let a = (lo[axis] - origin[axis]) / direction[axis]; let b = (hi[axis] - origin[axis]) / direction[axis];
            minimum = max(minimum, min(a, b)); maximum = min(maximum, max(a, b));
            if (minimum > maximum) { return false; }
        }
    }
    return true;
}

// xyz = A/B/C barycentrics; w = parameter. Negative barycentric sentinel means miss.
fn instance_trace_triangle(pa: vec3<f32>, pb: vec3<f32>, pc: vec3<f32>, origin: vec3<f32>, direction: vec3<f32>) -> vec4<f32> {
    let miss = vec4<f32>(-1.0);
    if (all(cross(pb - pa, pc - pa) == vec3<f32>(0.0))) { return miss; }
    let magnitude = abs(direction); var z = select(0u, 1u, magnitude.y > magnitude.x);
    if (magnitude.z > magnitude[z]) { z = 2u; }
    var x = (z + 1u) % 3u; var y = (x + 1u) % 3u;
    if (direction[z] < 0.0) { let swap = x; x = y; y = swap; }
    let sx = direction[x] / direction[z]; let sy = direction[y] / direction[z];
    let a = pa - origin; let b = pb - origin; let c = pc - origin;
    let ax = a[x] - sx * a[z]; let ay = a[y] - sy * a[z];
    let bx = b[x] - sx * b[z]; let by = b[y] - sy * b[z];
    let cx = c[x] - sx * c[z]; let cy = c[y] - sy * c[z];
    let edge = vec3<f32>(cx * by - cy * bx, ax * cy - ay * cx, bx * ay - by * ax);
    if (any(edge < vec3<f32>(0.0)) && any(edge > vec3<f32>(0.0))) { return miss; }
    let determinant = edge.x + edge.y + edge.z;
    if (determinant == 0.0) { return miss; }
    let barycentric = edge / determinant;
    return vec4<f32>(barycentric, dot(barycentric, vec3<f32>(a[z], b[z], c[z])) / direction[z]);
}

fn instance_trace_material(binding: vec4<u32>, slot: u32) -> u32 {
    var first = 0u; var last = binding.z;
    let offset = instance_trace_instances[1].z + binding.y;
    loop {
        if (first >= last) { break; }
        let at = first + (last - first) / 2u; let value = instance_trace_instances[offset + at];
        if (value.x == slot) { return value.y; }
        if (value.x < slot) { first = at + 1u; } else { last = at; }
    }
    return 0xffffffffu;
}

// Inclusive finite range, both sides and all materials. Instance indices address original stable-ID rows.
fn trace_instances(origin: vec3<f32>, direction: vec3<f32>, minimum: f32, maximum: f32) -> InstanceTraceHit {
    var hit = InstanceTraceHit(maximum, 0xffffffffu, 0xffffffffu, 0xffffffffu, 0xffffffffu, vec3<f32>(0.0), false, vec3<f32>(0.0));
    if (arrayLength(&instance_trace_geometry) < 3u || arrayLength(&instance_trace_instances) < 5u) { return hit; }
    let header = instance_trace_instances[0]; let tables = instance_trace_instances[1];
    if (instance_trace_geometry[0].w != 0x32475453u || instance_trace_instances[2].z != 0x31495453u
        || instance_trace_geometry[0].z != arrayLength(&instance_trace_geometry)
        || instance_trace_instances[2].y != arrayLength(&instance_trace_instances)
        || any(instance_trace_geometry[1] != instance_trace_instances[3])
        || any(instance_trace_geometry[2] != instance_trace_instances[4])) { return hit; }
    if (minimum < 0.0 || maximum < minimum || any(origin != origin) || any(direction != direction)
        || any(abs(origin) > vec3<f32>(3.402823466e38)) || any(abs(direction) > vec3<f32>(3.402823466e38))
        || minimum != minimum || maximum != maximum || maximum > 3.402823466e38 || all(direction == vec3<f32>(0.0))) { return hit; }
    var best_rank = 0xffffffffu; var top = 0u;
    loop {
        if (top >= header.x) { break; }
        let lo = instance_trace_instances[header.y + top * 3u]; let hi = instance_trace_instances[header.y + top * 3u + 1u];
        if (!instance_trace_box(bitcast<vec3<f32>>(lo.xyz), bitcast<vec3<f32>>(hi.xyz), origin, direction, minimum, hit.parameter)) {
            top = lo.w; continue;
        }
        let count = instance_trace_instances[header.y + top * 3u + 2u].x;
        for (var item = hi.w; item < hi.w + count; item++) {
            let index = instance_trace_instances[instance_trace_instances[2].x + item / 4u][item % 4u];
            let start = header.w + index * 5u; let instance = instance_trace_instances[start];
            let binding = instance_trace_instances[tables.x + instance.x];
            let row0 = bitcast<vec4<f32>>(instance_trace_instances[start + 2u]);
            let row1 = bitcast<vec4<f32>>(instance_trace_instances[start + 3u]);
            let row2 = bitcast<vec4<f32>>(instance_trace_instances[start + 4u]);
            let delta = origin - vec3<f32>(row0.w, row1.w, row2.w);
            let local_origin = vec3<f32>(dot(row0.xyz, delta), dot(row1.xyz, delta), dot(row2.xyz, delta));
            let local_direction = vec3<f32>(dot(row0.xyz, direction), dot(row1.xyz, direction), dot(row2.xyz, direction));
            let mesh = instance_trace_geometry[instance_trace_geometry[0].y + binding.x * 2u];
            let vertices = instance_trace_geometry[instance_trace_geometry[0].y + binding.x * 2u + 1u].x;
            var node = 0u;
            loop {
                if (node >= mesh.y) { break; }
                let node_lo = instance_trace_geometry[mesh.x + node * 2u]; let node_hi = instance_trace_geometry[mesh.x + node * 2u + 1u];
                if (!instance_trace_box(bitcast<vec3<f32>>(node_lo.xyz), bitcast<vec3<f32>>(node_hi.xyz), local_origin, local_direction, minimum, hit.parameter)) {
                    node = node_lo.w; continue;
                }
                let triangles = node_hi.w & 127u;
                let first = node_hi.w >> 7u;
                for (var t = first; t < first + triangles; t++) {
                    let base = mesh.z + t * 5u;
                    let triangle = vec4<u32>(instance_trace_word(base), instance_trace_word(base + 1u), instance_trace_word(base + 2u), instance_trace_word(base + 3u));
                    let a = bitcast<vec3<f32>>(instance_trace_geometry[vertices + triangle.x].xyz);
                    let b = bitcast<vec3<f32>>(instance_trace_geometry[vertices + triangle.y].xyz);
                    let c = bitcast<vec3<f32>>(instance_trace_geometry[vertices + triangle.z].xyz);
                    let result = instance_trace_triangle(a, b, c, local_origin, local_direction);
                    if (result.x < 0.0 || result.w != result.w || result.w < minimum || result.w > hit.parameter) { continue; }
                    if (result.w == hit.parameter && hit.instance != 0xffffffffu
                        && (instance.y > best_rank || (instance.y == best_rank && triangle.w >= hit.primitive))) { continue; }
                    let slot = instance_trace_word(base + 4u);
                    let material = instance_trace_material(binding, slot);
                    if (material == 0xffffffffu) { continue; }
#ifdef INSTANCE_TRACE_FILTER
                    if (!instance_trace_accept_material(material)) { continue; }
#endif
                    let normal = cross(b - a, c - a);
                    let world_normal = row0.xyz * normal.x + row1.xyz * normal.y + row2.xyz * normal.z;
                    let normal_scale = max(max(abs(world_normal.x), abs(world_normal.y)), abs(world_normal.z));
                    hit = InstanceTraceHit(result.w, instance.z, triangle.w, slot, material,
                        result.xyz, dot(normal, local_direction) < 0.0, normalize(world_normal / normal_scale));
                    best_rank = instance.y;
                }
                node++;
            }
        }
        top++;
    }
    return hit;
}
