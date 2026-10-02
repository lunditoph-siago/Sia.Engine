#define_import_path pbr/stream_expand

#import pbr/stream_selection_types
#import pbr/stream_node_types

@group(0) @binding(0) var<uniform> selection: StreamSelection;
@group(0) @binding(1) var<storage, read> nodes: array<Node>;
@group(0) @binding(2) var<storage, read> parts: array<vec4<u32>>;
@group(0) @binding(3) var<storage, read> residency: array<vec4<u32>>;
@group(0) @binding(4) var<storage, read_write> work: array<vec2<u32>>;
@group(0) @binding(5) var<storage, read> args: array<u32>;

@compute @workgroup_size(64)
fn expand(@builtin(workgroup_id) group: vec3<u32>, @builtin(local_invocation_index) lane: u32) {
    let selected = group.y * 65535u + group.x;
    if (selected >= args[12]) { return; }
    let record = selection.output.w + selected * 2u;
    let entry = work[record];
    let n = nodes[entry.x];
    var at = work[record + 1u].x;
    for (var p = 0u; p < n.links.w; p++) {
        let part = parts[n.links.z + p];
        let page = residency[part.x];
        for (var t = lane; t < part.z; t += 64u) {
            work[at + t] = vec2<u32>(page.x + part.y + t, entry.y);
        }
        at += part.z;
    }
}
