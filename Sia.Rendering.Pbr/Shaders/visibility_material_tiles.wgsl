#import pbr::visibility
#import pbr::materials

@group(1) @binding(0) var visibility: texture_2d<u32>;
@group(1) @binding(1) var<storage, read_write> tiles: array<atomic<u32>>;
@group(1) @binding(2) var<storage, read_write> dispatches: array<atomic<u32>>;
@group(1) @binding(3) var<storage, read> material_parameters: array<VisibilityMaterial>;
var<workgroup> materials: array<atomic<u32>, 4>;
var<workgroup> first_material: u32;

fn material_at(pixel: vec2<i32>) -> u32 {
    let id = textureLoad(visibility, pixel, 0).x;
    if (id == 0u || id > visibility_camera.size_counts.z) { return 0u; }
    let material = u32(visibility_instances[visibility_triangle_work(id - 1u).y].material.z);
    return material_parameters[material].indices.y;
}

@compute @workgroup_size(64)
fn reset(@builtin(global_invocation_id) thread: vec3<u32>) {
    let material = thread.x;
    if (material >= arrayLength(&dispatches) / 3u) { return; }
    let size = (visibility_camera.size_counts.xy + 7u) / 8u;
    atomicStore(&tiles[material * (size.x * size.y + 1u)], 0u);
    atomicStore(&dispatches[material * 3u], 0u);
    atomicStore(&dispatches[material * 3u + 1u], 0u);
    atomicStore(&dispatches[material * 3u + 2u], 1u);
}

@compute @workgroup_size(8, 8)
fn classify(@builtin(global_invocation_id) thread: vec3<u32>,
    @builtin(local_invocation_index) local: u32, @builtin(workgroup_id) group: vec3<u32>) {
    if (local < 4u) { atomicStore(&materials[local], 0u); }
    if (local == 0u) { first_material = material_at(vec2<i32>(group.xy * 8u)); }
    workgroupBarrier();
    let size = visibility_camera.size_counts.xy;
    if (all(thread.xy < size)) {
        let material = material_at(vec2<i32>(thread.xy));
        if (material < arrayLength(&dispatches) / 3u && (local == 0u || material != first_material)) {
            atomicOr(&materials[material / 32u], 1u << (material % 32u));
        }
    }
    workgroupBarrier();
    if (local < 4u) {
        let dimensions = (size + 7u) / 8u;
        let stride = dimensions.x * dimensions.y + 1u;
        var mask = atomicLoad(&materials[local]);
        while (mask != 0u) {
            let bit = firstTrailingBit(mask);
            let material = local * 32u + bit;
            let index = atomicAdd(&tiles[material * stride], 1u);
            atomicStore(&tiles[material * stride + 1u + index], group.y * dimensions.x + group.x);
            atomicMax(&dispatches[material * 3u], min(index + 1u, 65535u));
            atomicMax(&dispatches[material * 3u + 1u], index / 65535u + 1u);
            mask &= mask - 1u;
        }
    }
}
