#define_import_path pbr/tiles

#import pbr/bindings

@group(3) @binding(0) var ids: texture_2d<u32>;
@group(3) @binding(2) var<storage, read_write> tile_data: array<atomic<u32>>;

var<workgroup> mask: array<atomic<u32>, 8>;

fn stride() -> u32 {
#if COMPATIBILITY_UNIFORM_PADDING
    let padding = bitcast<u32>(frame.directional[frame.size.z & 3u].radiance.w);
    let tiles = (frame.size.xy + vec2<u32>(padding) + 7u) / 8u;
#else
    let tiles = (frame.size.xy + 7u) / 8u;
#endif
    return tiles.x * tiles.y + 4u;
}

@compute @workgroup_size(64)
fn reset_tiles(@builtin(global_invocation_id) id: vec3<u32>) {
    if (id.x >= frame.size.z) {
        return;
    }
    let base = id.x * stride();
    atomicStore( & tile_data[base], 0u);
    atomicStore( & tile_data[base + 1u], 1u);
    atomicStore( & tile_data[base + 2u], 1u);
    atomicStore( & tile_data[base + 3u], 0u);
}

@compute @workgroup_size(8, 8)
fn classify(
    @builtin(global_invocation_id) id: vec3<u32>,
    @builtin(local_invocation_index) local: u32,
    @builtin(workgroup_id) tile: vec3<u32>
) {
    if (local < 8u) {
        atomicStore( & mask[local], 0u);
    }
    workgroupBarrier();
    if (all(id.xy < frame.size.xy)) {
        let visible = textureLoad(ids, vec2<i32>(id.xy), 0).x;
        if (valid_triangle_id(visible)) {
            let material = triangle_material(visible - 1u);
            let batch_id = materials[material].indices.y;
            atomicOr( & mask[batch_id / 32u], 1u << (batch_id % 32u));
        }
    }
    workgroupBarrier();
    if (local < 8u) {
        var bits = atomicLoad( & mask[local]);
        while (bits != 0u) {
            let bit = firstTrailingBit(bits);
            let base = (local * 32u + bit) * stride();
            let index = atomicAdd( & tile_data[base + 3u], 1u);
            atomicStore( & tile_data[base + 4u + index], tile.x | (tile.y << 16u));
            atomicMax( & tile_data[base], min(index + 1u, 65535u));
            atomicMax( & tile_data[base + 1u], index / 65535u + 1u);
            bits &= bits - 1u;
        }
    }
}
