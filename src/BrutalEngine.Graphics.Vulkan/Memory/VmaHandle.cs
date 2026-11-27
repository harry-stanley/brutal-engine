using Brutal.VulkanApi.Vma;

namespace BrutalEngine.Graphics.Vulkan.Memory;

/// <summary>
/// A device's VMA allocator (docs/design/graphics-vulkan.md section 6, "Only the raw, result-returning calls"): the raw
/// handle, so the generated, result-returning VMA calls (<c>VmaAllocatorEx</c>, generic over <see cref="IVmaAllocator"/>)
/// reach it without BRUTAL's throwing <c>VmaAllocator</c> class.
/// </summary>
/// <remarks>
/// <para>
/// Created only by <see cref="VmaAllocatorFactory.Create(Devices.VulkanDeviceCore, VulkanCallGate)"/>. A value type:
/// the generated calls take it as a constrained generic, so nothing is boxed, and copies share the one allocator. The
/// owner keeps exactly one copy as the owning one and calls <see cref="Destroy"/> on it once, after every allocation
/// made from it is freed and before the device is destroyed; VMA doesn't detect a second destroy.
/// </para>
/// <para>
/// Not thread-safe: the device's owner thread uses and destroys it.
/// </para>
/// </remarks>
internal readonly struct VmaHandle : IVmaAllocator
{
    /// <summary>Wraps a handle <c>vmaCreateAllocator</c> returned.</summary>
    /// <param name="handle">The allocator.</param>
    internal VmaHandle(VmaAllocatorHandle handle) => VkHandle = handle;

    /// <summary>Gets the raw allocator handle; the null handle for <see langword="default"/>.</summary>
    public VmaAllocatorHandle VkHandle { get; }

    /// <summary>Gets whether this is the null handle (<see langword="default"/>).</summary>
    public bool IsNull => VkHandle.IsNull();

    /// <summary>
    /// Destroys the allocator with <c>vmaDestroyAllocator</c>, which frees the device memory blocks VMA still holds. Call
    /// it once, on the owning copy; the null handle does nothing, as VMA ignores it.
    /// </summary>
    public void Destroy() => Vma.PInvoke.vmaDestroyAllocator(VkHandle);
}
