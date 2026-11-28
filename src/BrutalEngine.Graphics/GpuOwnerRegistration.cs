using System;
using BrutalEngine.Core;

namespace BrutalEngine.Graphics;

/// <summary>
/// A resource owner's registration with one device, from <see cref="GraphicsDevice.RegisterOwner"/>: dispose it to
/// unregister (docs/design/graphics.md section 12).
/// </summary>
/// <remarks>
/// Owned by the caller of <see cref="GraphicsDevice.RegisterOwner"/>. It holds the device and the owner, and nothing
/// native. Owner thread of its device.
/// </remarks>
public sealed class GpuOwnerRegistration : IDisposable
{
    private readonly GraphicsDevice _device;

    internal GpuOwnerRegistration(GraphicsDevice device, IGpuResourceOwner owner)
    {
        _device = device;
        Owner = owner;
    }

    /// <summary>The registered owner.</summary>
    internal IGpuResourceOwner Owner { get; }

    /// <summary>Unregisters the owner: the device's recoveries no longer call it. Idempotent.</summary>
    /// <exception cref="WrongThreadException">
    /// Called from a thread other than the device's owner thread, also when already unregistered.
    /// </exception>
    /// <remarks>
    /// A second call, or a call after the device's <see cref="GraphicsDevice.Dispose"/>, does nothing. Allocates nothing,
    /// except the exception.
    /// </remarks>
    public void Dispose() => _device.Unregister(this);
}
