using System;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Vma;
using BrutalEngine.Graphics.Backend;

namespace BrutalEngine.Graphics.Vulkan.Memory;

/// <summary>
/// Maps the contract's <see cref="BackendMemory"/> to VMA's allocation description (docs/design/graphics-vulkan.md
/// section 6, "Memory placement").
/// </summary>
/// <remarks>
/// <para>The table:</para>
/// <list type="table">
/// <listheader><term>Memory</term><description>VMA usage; flags; required and preferred properties</description></listheader>
/// <item><term><see cref="BackendMemory.DeviceLocal"/></term><description><c>AUTO_PREFER_DEVICE</c>; <c>WITHIN_BUDGET</c>; none.</description></item>
/// <item><term><see cref="BackendMemory.DeviceLocalHostVisiblePreferred"/></term><description><c>AUTO_PREFER_DEVICE</c>;
/// <c>WITHIN_BUDGET</c>; <c>HOST_VISIBLE</c> preferred (a placement hint for resizable BAR, never mapped).</description></item>
/// <item><term><see cref="BackendMemory.HostUpload"/></term><description><c>AUTO</c>; <c>HOST_ACCESS_SEQUENTIAL_WRITE</c>,
/// <c>MAPPED</c>, <c>WITHIN_BUDGET</c>; <c>HOST_VISIBLE</c> and <c>HOST_COHERENT</c> required.</description></item>
/// <item><term><see cref="BackendMemory.HostReadback"/></term><description><c>AUTO</c>; <c>HOST_ACCESS_RANDOM</c>,
/// <c>MAPPED</c>, <c>WITHIN_BUDGET</c>; <c>HOST_VISIBLE</c> and <c>HOST_COHERENT</c> required, <c>HOST_CACHED</c>
/// preferred.</description></item>
/// </list>
/// <para>
/// <c>MAPPED</c> keeps the host kinds mapped for their lifetime, and <c>HOST_COHERENT</c> makes CPU writes visible to
/// the next submission with no flush; the specification guarantees a host-visible, host-coherent memory type on every
/// device. <c>WITHIN_BUDGET</c> makes an allocation over the heap's budget fail as out of device memory instead of
/// paging. Every other field stays zero: any memory type, the default pool, no user data, priority and alignment.
/// </para>
/// <para>Pure, stateless and thread-safe. Allocates nothing.</para>
/// </remarks>
internal static class MemoryPlacement
{
    /// <summary>Gives the allocation description for a memory kind.</summary>
    /// <param name="memory">The kind.</param>
    /// <returns>The description, as the table gives it.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="memory"/> isn't a <see cref="BackendMemory"/> member.</exception>
    public static VmaAllocationCreateInfo For(BackendMemory memory) => memory switch
    {
        BackendMemory.DeviceLocal => Info(VmaMemoryUsage.AutoPreferDevice, VmaAllocationCreateFlags.WithinBudgetBit, 0, 0),
        BackendMemory.DeviceLocalHostVisiblePreferred => Info(
            VmaMemoryUsage.AutoPreferDevice,
            VmaAllocationCreateFlags.WithinBudgetBit,
            0,
            VkMemoryPropertyFlags.HostVisibleBit),
        BackendMemory.HostUpload => Info(
            VmaMemoryUsage.Auto,
            VmaAllocationCreateFlags.HostAccessSequentialWriteBit | VmaAllocationCreateFlags.MappedBit | VmaAllocationCreateFlags.WithinBudgetBit,
            VkMemoryPropertyFlags.HostVisibleBit | VkMemoryPropertyFlags.HostCoherentBit,
            0),
        BackendMemory.HostReadback => Info(
            VmaMemoryUsage.Auto,
            VmaAllocationCreateFlags.HostAccessRandomBit | VmaAllocationCreateFlags.MappedBit | VmaAllocationCreateFlags.WithinBudgetBit,
            VkMemoryPropertyFlags.HostVisibleBit | VkMemoryPropertyFlags.HostCoherentBit,
            VkMemoryPropertyFlags.HostCachedBit),
        _ => ThrowUnknownMemory(memory),
    };

    private static VmaAllocationCreateInfo Info(
        VmaMemoryUsage usage,
        VmaAllocationCreateFlags flags,
        VkMemoryPropertyFlags required,
        VkMemoryPropertyFlags preferred) =>
        new() { Usage = usage, Flags = flags, RequiredFlags = required, PreferredFlags = preferred };

    private static VmaAllocationCreateInfo ThrowUnknownMemory(BackendMemory memory) =>
        throw new ArgumentOutOfRangeException(nameof(memory), (int)memory, "Not a BackendMemory member.");
}
