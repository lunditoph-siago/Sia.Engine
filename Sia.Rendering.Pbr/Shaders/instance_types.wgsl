#define_import_path pbr/instance_types

// Matches the 144-byte PbrInstanceGpu CPU layout.
struct Instance {
    transform: mat4x4<f32>,
    normal_transform: mat4x4<f32>,
    material: vec4<u32> // material index, bitcast spatial-error scale, source-slot+1, dynamic flag
}
