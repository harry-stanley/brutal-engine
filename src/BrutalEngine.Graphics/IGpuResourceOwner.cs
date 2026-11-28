using BrutalEngine.Core;

namespace BrutalEngine.Graphics;

/// <summary>
/// Something that owns GPU resources it can make again after a device loss: the overlay, a renderer, an asset database
/// (docs/design/graphics.md section 12). It registers with <see cref="GraphicsDevice.RegisterOwner"/>.
/// </summary>
/// <remarks>
/// The device calls both members on its owner thread, from its recovery, never re-entrantly. An implementation keeps
/// enough on the CPU side (descriptions, source bytes or a way to reload them) to make its resources again.
/// </remarks>
public interface IGpuResourceOwner
{
    /// <summary>The device is being recovered: drop every handle of this device, which is stale from now on.</summary>
    /// <param name="device">The recovering device.</param>
    /// <remarks>
    /// Called in reverse registration order, before the old resources are discarded. Don't call the device: it has no
    /// usable backend device at this point.
    /// </remarks>
    public void OnDeviceLost(GraphicsDevice device);

    /// <summary>The device has a new backend device: make the resources again and upload their contents.</summary>
    /// <param name="device">The recovered device, usable again.</param>
    /// <returns>Success, or the failure that makes recovery fail; the device's recovery returns it.</returns>
    /// <remarks>Called in registration order, once every swapchain has been recreated in place.</remarks>
    public Result OnDeviceRecreated(GraphicsDevice device);
}
