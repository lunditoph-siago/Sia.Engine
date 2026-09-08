#import pbr::occlusion

struct Camera {
    view_projection: mat4x4<f32>, eye: vec4<f32>, size_counts: vec4<u32>,
    light_direction: vec4<f32>, light_radiance: vec4<f32>, raster: vec4<u32>, raster_origin: vec4<f32>,
}
struct Instance {
    transform: mat4x4<f32>, normal_transform: mat4x4<f32>,
    color: vec4<f32>, material: vec4<f32>, emissive: vec4<f32>, roots: vec4<u32>,
}
struct Cluster { minimum: vec4<f32>, maximum: vec4<f32>, sphere: vec4<f32>, cone: vec4<f32>, work: vec4<u32> }
@group(0) @binding(0) var<uniform> camera: Camera;
@group(0) @binding(1) var<storage, read> clusters: array<Cluster>;
@group(0) @binding(2) var<storage, read> instances: array<Instance>;
@group(0) @binding(3) var<storage, read_write> prefix: array<vec2<u32>>;
@group(0) @binding(4) var<storage, read_write> blocks: array<vec2<u32>>;
@group(0) @binding(5) var<storage, read_write> work: array<vec2<u32>>;
@group(0) @binding(6) var<storage, read_write> draw: array<u32>;
@group(0) @binding(7) var<storage, read> topology: array<u32>;
@group(0) @binding(8) var<storage, read_write> indices: array<u32>;
var<workgroup> sums: array<vec2<u32>, 256>;

fn backfacing(cluster: Cluster) -> bool {
    if (cluster.cone.w >= 1.0) { return false; }
    let origin = transpose(instances[cluster.work.y].normal_transform) * camera.raster_origin;
    let direction = origin.xyz - cluster.sphere.xyz * origin.w;
    let distance = length(direction);
    let bound = distance * cluster.cone.w + cluster.sphere.w * abs(origin.w);
    let facing = dot(direction, cluster.cone.xyz);
    if (any((bitcast<vec2<u32>>(vec2<f32>(bound, facing)) & vec2<u32>(0x7f800000u)) == vec2<u32>(0x7f800000u))) { return false; }
    return facing < -bound - max(1e-6, distance * 1e-5);
}

fn outside_frustum(cluster: Cluster) -> bool {
    let matrix = camera.view_projection * instances[cluster.work.y].transform;
    var outside_xy = vec4<bool>(true);
    var outside_z = vec2<bool>(true);
    for (var corner = 0u; corner < 8u; corner++) {
        let point = select(cluster.minimum.xyz, cluster.maximum.xyz,
            (vec3<u32>(corner) & vec3<u32>(1u, 2u, 4u)) != vec3<u32>(0u));
        var clip = matrix * vec4<f32>(point, 1.0);
        if (any((bitcast<vec4<u32>>(clip) & vec4<u32>(0x7f800000u)) == vec4<u32>(0x7f800000u))) { return false; }
        let magnitude = max(max(abs(clip.x), abs(clip.y)), max(abs(clip.z), abs(clip.w)));
        if (magnitude < 1.17549435e-38 || magnitude > 8.50705917e37) { return false; }
        clip /= magnitude;
        outside_xy &= vec4<f32>(clip.x + clip.w, clip.w - clip.x, clip.y + clip.w, clip.w - clip.y) < vec4<f32>(-1e-5);
        outside_z &= vec2<f32>(clip.z, clip.w - clip.z) < vec2<f32>(-1e-5);
    }
    return any(outside_xy) || any(outside_z);
}

fn inclusive_sum(lane: u32, value: vec2<u32>) -> vec2<u32> {
    sums[lane] = value;
    workgroupBarrier();
    for (var step = 1u; step < 256u; step *= 2u) {
        var add = vec2<u32>(0u);
        if (lane >= step) { add = sums[lane - step]; }
        workgroupBarrier();
        sums[lane] += add;
        workgroupBarrier();
    }
    return sums[lane];
}

@compute @workgroup_size(256)
fn cull(@builtin(workgroup_id) group: vec3<u32>, @builtin(local_invocation_index) lane: u32) {
    let block = group.x + group.y * camera.raster.z;
    let count = camera.raster.y;
    if (block >= (count + 255u) / 256u) { return; }
    let index = block * 256u + lane;
    var visible = vec2<u32>(0u);
    if (index < count && !outside_frustum(clusters[index]) && !backfacing(clusters[index])) {
        visible = vec2<u32>(1u, clusters[index].work.z);
    }
    let sum = inclusive_sum(lane, visible);
    if (index < count) { prefix[index] = (sum - visible) | vec2<u32>(visible.x << 31u, 0u); }
    if (lane == 255u) { blocks[block] = sum; }
}

fn cull_occlusion(group: vec3<u32>, lane: u32, post: bool) {
    let block = group.x + group.y * camera.raster.z;
    let count = camera.raster.y;
    if (block >= (count + 255u) / 256u) { return; }
    let index = block * 256u + lane;
    var visible = vec2<u32>(0u);
    var deferred = 0u;
    if (index < count) {
        let cluster = clusters[index];
        let transform = instances[cluster.work.y].transform;
        if (post) {
            if ((prefix[index].x & 0x40000000u) != 0u
                && !occluded(cluster.minimum.xyz, cluster.maximum.xyz, camera.view_projection * transform)) {
                visible = vec2<u32>(1u, cluster.work.z);
            }
        } else if (!outside_frustum(cluster) && !backfacing(cluster)) {
            if (hierarchy.size.w != 0u && occluded(cluster.minimum.xyz, cluster.maximum.xyz, hierarchy.previous_projection * transform)) {
                deferred = 0x40000000u;
            } else { visible = vec2<u32>(1u, cluster.work.z); }
        }
    }
    let sum = inclusive_sum(lane, visible);
    if (index < count) { prefix[index] = (sum - visible) | vec2<u32>((visible.x << 31u) | deferred, 0u); }
    if (lane == 255u) { blocks[block] = sum; }
}

@compute @workgroup_size(256)
fn cull_main(@builtin(workgroup_id) group: vec3<u32>, @builtin(local_invocation_index) lane: u32) {
    cull_occlusion(group, lane, false);
}

@compute @workgroup_size(256)
fn cull_post(@builtin(workgroup_id) group: vec3<u32>, @builtin(local_invocation_index) lane: u32) {
    cull_occlusion(group, lane, true);
}

fn scan_blocks(lane: u32, post: bool) {
    let count = (camera.raster.y + 255u) / 256u;
    var carry = vec2<u32>(0u);
    for (var base = 0u; base < count; base += 256u) {
        var value = vec2<u32>(0u);
        if (base + lane < count) { value = blocks[base + lane]; }
        let sum = inclusive_sum(lane, value);
        if (base + lane < count) { blocks[base + lane] = carry + sum - value; }
        carry += sums[255];
        workgroupBarrier();
    }
    if (lane == 0u) {
        draw[0] = carry.y * 3u; draw[1] = 1u; draw[2] = 0u; draw[3] = 0u; draw[4] = 0u;
        draw[6] = select(0u, draw[5], post);
        draw[5] = draw[6] + carry.x;
        draw[7] = select(0u, draw[7], post) + carry.y;
    }
}

@compute @workgroup_size(256)
fn scan(@builtin(local_invocation_index) lane: u32) { scan_blocks(lane, false); }

@compute @workgroup_size(256)
fn scan_post(@builtin(local_invocation_index) lane: u32) { scan_blocks(lane, true); }

fn emit_cluster(index: u32, lane: u32, post: bool) {
    if (index >= camera.raster.y) { return; }
    let entry = prefix[index];
    if ((entry.x >> 31u) == 0u) { return; }
    var offset = blocks[index / 256u] + (entry & vec2<u32>(0x3fffffffu, 0xffffffffu));
    if (post) { offset.x += draw[6]; }
    let cluster = clusters[index].work;
    if (lane == 0u) { work[offset.x] = cluster.xy; }
    for (var triangle = lane; triangle < cluster.z; triangle += 64u) {
        let packed = topology[cluster.w + triangle];
        let base = (offset.y + triangle) * 3u;
        indices[base] = select(((offset.x * 256u + (packed & 255u)) << 1u),
            (((offset.x * camera.raster.x + triangle) * 3u) << 1u) | 1u, camera.raster.w != 2u);
        if (camera.raster.w == 1u) { indices[base] = ((offset.x * camera.raster.x + triangle) << 1u) | 1u; }
        indices[base + 1u] = select((offset.x * 256u + ((packed >> 8u) & 255u)) << 1u,
            (((offset.x * camera.raster.x + triangle) * 3u + 1u) << 1u) | 1u, camera.raster.w == 3u);
        indices[base + 2u] = select((offset.x * 256u + ((packed >> 16u) & 255u)) << 1u,
            (((offset.x * camera.raster.x + triangle) * 3u + 2u) << 1u) | 1u, camera.raster.w == 3u);
    }
}

fn emit_indices(group: vec3<u32>, lane: u32, post: bool) {
    let step = min(max(1u, camera.raster.y), camera.raster.z * camera.raster.z);
    for (var index = group.x + group.y * camera.raster.z; index < camera.raster.y; index += step) {
        emit_cluster(index, lane, post);
    }
}

@compute @workgroup_size(64)
fn emit(@builtin(workgroup_id) group: vec3<u32>, @builtin(local_invocation_index) lane: u32) { emit_indices(group, lane, false); }

@compute @workgroup_size(64)
fn emit_post(@builtin(workgroup_id) group: vec3<u32>, @builtin(local_invocation_index) lane: u32) { emit_indices(group, lane, true); }
