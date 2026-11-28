using System;
using Brutal.Pointers;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Vma;
using BrutalEngine.Core.Logging;
using BrutalEngine.Graphics.Vulkan.Memory;

namespace BrutalEngine.Graphics.Vulkan.Resources;

/// <summary>
/// Logs every memory heap's usage and budget when an allocation runs out of memory (docs/design/graphics-vulkan.md
/// section 6, "Budgets"), so the log shows which heap was exhausted.
/// </summary>
/// <remarks>
/// <para>
/// One <c>Warning</c> per heap, in heap order, for example
/// <c>Out of GPU memory: heap 0 (device-local, 8589934592 bytes) has 1048576 bytes in use of a 7730941132-byte budget</c>.
/// The heaps come from VMA's copy of the adapter's memory properties (<c>vmaGetMemoryProperties</c>,
/// <c>Brutal.Vulkan.Vma/Generated/VmaAllocatorEx.gen.cs:43-55</c>) and the budgets from <c>vmaGetHeapBudgets</c>
/// (<c>Brutal.Vulkan.Vma/Generated/VmaAllocatorEx.gen.cs:104</c>): the driver's when the device enabled
/// <c>VK_EXT_memory_budget</c>, VMA's estimate otherwise.
/// </para>
/// <para>Stateless; called on the device's owner thread. Neither query can fail.</para>
/// </remarks>
internal static class HeapBudgetLog
{
    /// <summary>Logs a device's heaps.</summary>
    /// <param name="vma">The device's allocator.</param>
    /// <param name="log">Where the warnings go; <see langword="default"/> for nowhere.</param>
    /// <exception cref="ArgumentException"><paramref name="vma"/> is the null handle.</exception>
    public static void Write(VmaHandle vma, LogCategory log)
    {
        if (vma.IsNull)
        {
            throw new ArgumentException("The allocator is the null handle.", nameof(vma));
        }

        vma.GetMemoryProperties(out Ptr<VkPhysicalDeviceMemoryProperties> memory);
        Span<VmaBudget> budgets = stackalloc VmaBudget[VK.MAX_MEMORY_HEAPS];
        vma.GetHeapBudgets(budgets);
        Write(memory.Value, budgets, log);
    }

    /// <summary>Logs the given heaps and budgets.</summary>
    /// <param name="memory">The adapter's memory properties; its first <c>MemoryHeapCount</c> heaps are logged.</param>
    /// <param name="budgets">Each heap's budget, by heap index; at least <c>MemoryHeapCount</c> of them.</param>
    /// <param name="log">Where the warnings go.</param>
    /// <exception cref="ArgumentException"><paramref name="budgets"/> has fewer entries than there are heaps.</exception>
    internal static void Write(in VkPhysicalDeviceMemoryProperties memory, ReadOnlySpan<VmaBudget> budgets, LogCategory log)
    {
        int heaps = memory.MemoryHeapCount;
        if ((uint)heaps > (uint)budgets.Length)
        {
            throw new ArgumentException($"{heaps} heaps but {budgets.Length} budgets.", nameof(budgets));
        }

        for (int heap = 0; heap < heaps; heap++)
        {
            VkMemoryHeap properties = memory.MemoryHeaps[heap];
            ReadOnlySpan<byte> kind = (properties.Flags & VkMemoryHeapFlags.DeviceLocalBit) != 0 ? "device-local"u8 : "host"u8;
            ulong usage = budgets[heap].Usage.Value;
            ulong budget = budgets[heap].Budget.Value;
            log.Warning($"Out of GPU memory: heap {heap} ({kind}, {properties.Size.Value} bytes) has {usage} bytes in use of a {budget}-byte budget");
        }
    }
}
