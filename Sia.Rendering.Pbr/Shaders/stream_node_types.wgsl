#define_import_path pbr/stream_node_types

struct Node {
    lo: vec4<f32>, hi: vec4<f32>,
    links: vec4<u32>, // first child, child count, first part, part count
    owner: vec4<u32>  // parent, unused, unused, unused
}
