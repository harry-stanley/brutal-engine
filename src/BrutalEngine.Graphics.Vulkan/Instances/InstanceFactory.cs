using System;

namespace BrutalEngine.Graphics.Vulkan.Instances;

/// <summary>
/// Creates the device's Vulkan instance (docs/design/graphics-vulkan.md section 1). Holds the request
/// <see cref="InstanceExtensions.Plan"/> reads.
/// </summary>
internal static class InstanceFactory
{
    /// <summary>
    /// The instance's part of a device request: a stand-in for <c>BackendDeviceRequest</c> (docs/design/graphics.md,
    /// "The backend interface"), which doesn't exist yet. The device fills it from the request's fields.
    /// </summary>
    internal sealed record Options
    {
        /// <summary>Gets the application name; empty by default.</summary>
        public string ApplicationName { get; init; } = string.Empty;

        /// <summary>Gets whether the validation layer, synchronization validation and <c>VK_EXT_debug_utils</c> are required.</summary>
        public bool Validation { get; init; }

        /// <summary>Gets the compatible surface's required instance extensions; empty for an offscreen-only device.</summary>
        public ReadOnlyMemory<string> SurfaceExtensions { get; init; }
    }
}
