using Sia.WebGPU;

namespace Sia.Engine.Rendering;

public static class WebGpuCapabilities
{
    public static unsafe RenderCapabilities Read(WgpuHandle<WGPUDevice> device)
    {
        var limits = Wgpu.GetLimits(device);
        return new(limits.MaxBufferSize, limits.MaxStorageBufferBindingSize, limits.MaxTextureDimension2D,
            limits.MaxTextureArrayLayers,
            WgpuUnsafe.wgpuDeviceHasFeature((WGPUDevice*)device.DangerousGetHandle(), WGPUFeatureName.TimestampQuery) != 0);
    }
}
