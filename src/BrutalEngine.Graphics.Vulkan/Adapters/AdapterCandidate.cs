using System;
using Brutal.VulkanApi;

namespace BrutalEngine.Graphics.Vulkan.Adapters;

/// <summary>
/// What the adapter rules need to know about one Vulkan physical device: plain data, read from the driver during
/// enumeration or built by hand in tests (docs/design/graphics-vulkan.md section 3, "Candidates").
/// </summary>
/// <remarks>
/// <para>
/// Every feature flag is <see langword="false"/> for a device that doesn't support it, including a device below
/// Vulkan 1.3 whose 1.3 features were never queried. Nothing here is validated: <c>AdapterRules</c> judges the
/// values as given.
/// </para>
/// <para>
/// Immutable and safe to read from any thread, provided nobody changes the arrays behind
/// <see cref="DeviceExtensions"/> and <see cref="QueueFamilies"/> after construction. Equality is the record's, so
/// those two compare by the memory they wrap, not by content. The default value is a device with no version, no
/// features, no extensions and no queue families.
/// </para>
/// </remarks>
internal readonly record struct AdapterCandidate
{
    /// <summary>Gets the device name as the driver reports it.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the kind of device, mapped by name from <c>VkPhysicalDeviceType</c>.</summary>
    public required AdapterType Type { get; init; }

    /// <summary>Gets the driver's <c>driverID</c>, which <c>DriverNames.Get</c> names.</summary>
    public required VkDriverId Driver { get; init; }

    /// <summary>Gets the major part of the Vulkan version the device supports.</summary>
    public required int ApiVersionMajor { get; init; }

    /// <summary>Gets the minor part of the Vulkan version the device supports.</summary>
    public required int ApiVersionMinor { get; init; }

    /// <summary>Gets the patch part of the Vulkan version the device supports.</summary>
    public required int ApiVersionPatch { get; init; }

    /// <summary>Gets a value indicating whether the device supports the Vulkan 1.3 <c>dynamicRendering</c> feature.</summary>
    public required bool DynamicRendering { get; init; }

    /// <summary>Gets a value indicating whether the device supports the Vulkan 1.3 <c>synchronization2</c> feature.</summary>
    public required bool Synchronization2 { get; init; }

    /// <summary>Gets a value indicating whether the device supports the <c>samplerAnisotropy</c> feature.</summary>
    public required bool SamplerAnisotropy { get; init; }

    /// <summary>Gets a value indicating whether the device supports the <c>textureCompressionBC</c> feature.</summary>
    public required bool TextureCompressionBC { get; init; }

    /// <summary>Gets a value indicating whether the device supports the <c>textureCompressionASTC_LDR</c> feature.</summary>
    public required bool TextureCompressionAstcLdr { get; init; }

    /// <summary>Gets the device limit <c>timestampPeriod</c>: nanoseconds per timestamp tick.</summary>
    public required float TimestampPeriod { get; init; }

    /// <summary>Gets the names of the device extensions the device offers, in the driver's order.</summary>
    public required ReadOnlyMemory<string> DeviceExtensions { get; init; }

    /// <summary>Gets the device's queue families, in the driver's order.</summary>
    public required ReadOnlyMemory<QueueFamily> QueueFamilies { get; init; }

    /// <summary>What the adapter rules need to know about one queue family of a candidate.</summary>
    /// <param name="Index">The Vulkan queue family index, which device creation passes to the driver.</param>
    /// <param name="Graphics">Whether the family's <c>queueFlags</c> include <c>GRAPHICS</c>.</param>
    /// <param name="Compute">Whether the family's <c>queueFlags</c> include <c>COMPUTE</c>.</param>
    /// <param name="Present">
    /// Whether the family can present to the request's compatible surface; <see langword="false"/> when there is no
    /// surface to ask about.
    /// </param>
    /// <param name="TimestampValidBits">The family's <c>timestampValidBits</c>; 0 means it can't write timestamps.</param>
    /// <remarks>Immutable; safe to read from any thread.</remarks>
    internal readonly record struct QueueFamily(int Index, bool Graphics, bool Compute, bool Present, int TimestampValidBits);
}
