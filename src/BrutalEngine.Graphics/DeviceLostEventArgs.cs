using System;

namespace BrutalEngine.Graphics;

/// <summary>
/// What <see cref="GraphicsDevice.DeviceLost"/> reports: where the loss was detected, the backend's status code, and
/// whether a hung GPU caused it (docs/design/graphics.md section 12).
/// </summary>
/// <remarks>
/// Immutable; made by the device once per loss and read on any thread. The device never keeps or reuses it.
/// </remarks>
public sealed class DeviceLostEventArgs : EventArgs
{
    internal DeviceLostEventArgs(string operation, int nativeCode, bool timedOut)
    {
        Operation = operation;
        NativeCode = nativeCode;
        TimedOut = timedOut;
    }

    /// <summary>
    /// The device member that detected the loss, without its type: <c>"BeginFrame"</c>, <c>"SubmitFrame"</c>,
    /// <c>"AcquireSwapchainImage"</c>, <c>"WaitIdle"</c>, or the creation or write that reported it
    /// (<c>"CreateBuffer"</c>, <c>"WriteTexture"</c>, <c>"AllocateTransient"</c>, ...).
    /// </summary>
    /// <remarks>Allocates nothing.</remarks>
    public string Operation { get; }

    /// <summary>The backend's status code for the loss (for Vulkan a <c>VkResult</c>); 0 when the backend gave none.</summary>
    /// <remarks>Allocates nothing.</remarks>
    public int NativeCode { get; }

    /// <summary>
    /// Whether the GPU timeout (<see cref="GraphicsDeviceOptions.GpuTimeout"/>) expired, a hung GPU, rather than the
    /// backend reporting the loss.
    /// </summary>
    /// <remarks>Allocates nothing.</remarks>
    public bool TimedOut { get; }
}
