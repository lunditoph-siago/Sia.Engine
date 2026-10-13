#define_import_path pbr/instance_types

// Matches the 144-byte PbrInstanceGpu CPU layout.
struct Instance {
    transform: mat4x4<f32>,
    normal_transform: mat4x4<f32>,
    // Stream: material, bitcast spatial-error scale, reserved, reserved.
    // Resident: material, original input instance, logical triangle start, count.
    material: vec4<u32>
}
