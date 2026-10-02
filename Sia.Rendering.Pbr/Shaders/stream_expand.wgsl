#define_import_path pbr/stream_expand

#import pbr/stream_selection_types
#import pbr/stream_node_types
#import pbr/stream_work_types

@group(0) @binding(0) var<uniform> selection: StreamSelection;
@group(0) @binding(1) var<storage, read> nodes: array<Node>;
@group(0) @binding(2) var<storage, read> parts: array<vec4<u32>>;
@group(0) @binding(3) var<storage, read> residency: array<vec4<u32>>;
@group(0) @binding(4) var<storage, read_write> work: array<vec4<u32>>;
@group(0) @binding(5) var<storage, read> args: array<u32>;

@compute @workgroup_size(64)
fn expand(@builtin(workgroup_id) group: vec3<u32>, @builtin(local_invocation_index) lane: u32) {
    let selected = group.y * 65535u + group.x;
    if (selected >= args[12]) { return; }
    let record = selection.output.w + selected;
    let entry = work[record];
    let n = nodes[entry.x];
    var at = entry.z;
    for (var p = 0u; p < n.links.w; p++) {
        let part = parts[n.links.z + p];
        let page = residency[part.x];
        let count = (part.z + STREAM_WORK_BLOCK_TRIANGLES - 1u) / STREAM_WORK_BLOCK_TRIANGLES;
        for (var r = lane; r < count; r += 64u) {
            let first = r * STREAM_WORK_BLOCK_TRIANGLES;
            work[at + r] = vec4<u32>(page.x + part.y + first, entry.y,
                min(STREAM_WORK_BLOCK_TRIANGLES, part.z - first), 0u);
        }
        at += count;
    }
}
