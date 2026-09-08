using Sia;
using Sia.WebGPU;

namespace Sia.Engine.Rendering.Pbr;

internal static unsafe class PbrTextureBindGroups
{
    public static Entity Create(World world, WgpuHandle<WGPUDevice> device, Entity layout,
        ReadOnlySpan<WGPUBindGroupEntry> entries, params ReadOnlySpan<WgpuHandle<WGPUTextureView>> views)
    {
        var retained = views.ToArray();
        WgpuHandle<WGPUBindGroup> owned;
        fixed (WGPUBindGroupEntry* pointer = entries) {
            var descriptor = WGPUBindGroupDescriptor.Default;
            descriptor.Layout = (WGPUBindGroupLayout*)layout.GetWgpu<WGPUBindGroupLayout>().DangerousGetHandle();
            descriptor.EntryCount = (nuint)entries.Length;
            descriptor.Entries = pointer;
            owned = Wgpu.CreateBindGroup(device, descriptor);
        }
        foreach (var view in retained) { WgpuUnsafe.wgpuTextureViewAddRef((WGPUTextureView*)view.DangerousGetHandle()); }
        try {
            return world.OwnWgpu(owned, (ref WgpuHandle<WGPUBindGroup> handle) => { Release(); handle = default; });
        }
        catch { Release(); throw; }

        void Release()
        {
            Wgpu.Release(ref owned);
            for (var i = retained.Length - 1; i >= 0; i--) { Wgpu.Release(ref retained[i]); }
        }
    }
}
