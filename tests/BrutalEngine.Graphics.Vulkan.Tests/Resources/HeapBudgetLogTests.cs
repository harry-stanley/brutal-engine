using System;
using Brutal;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Vma;
using BrutalEngine.Core.Logging;
using BrutalEngine.Graphics.Vulkan.Memory;
using BrutalEngine.Graphics.Vulkan.Resources;
using BrutalEngine.Graphics.Vulkan.Tests.Validation;
using Xunit;

namespace BrutalEngine.Graphics.Vulkan.Tests.Resources;

public sealed class HeapBudgetLogTests
{
    [Fact]
    public void Write_EachHeap_OneWarningWithItsUsageAndBudget()
    {
        // Three heaps, the middle one host memory, with distinct sizes, usages and budgets, so a swapped field or index
        // shows in the text.
        VkPhysicalDeviceMemoryProperties memory = Memory(
            (8_589_934_592, VkMemoryHeapFlags.DeviceLocalBit),
            (17_179_869_184, 0),
            (268_435_456, VkMemoryHeapFlags.DeviceLocalBit | VkMemoryHeapFlags.MultiInstanceBit));
        VmaBudget[] budgets = [Budget(1_048_576, 7_730_941_132), Budget(65_536, 8_589_934_592), Budget(0, 214_748_364)];
        ValidationLog sink = new();
        (Logger logger, LogCategory log) = ValidationLog.Create(sink);
        using (logger)
        {
            HeapBudgetLog.Write(memory, budgets, log);
        }

        Assert.Equal(
            [
                (LogLevel.Warning, "Out of GPU memory: heap 0 (device-local, 8589934592 bytes) has 1048576 bytes in use of a 7730941132-byte budget"),
                (LogLevel.Warning, "Out of GPU memory: heap 1 (host, 17179869184 bytes) has 65536 bytes in use of a 8589934592-byte budget"),
                (LogLevel.Warning, "Out of GPU memory: heap 2 (device-local, 268435456 bytes) has 0 bytes in use of a 214748364-byte budget"),
            ],
            sink.Entries);
    }

    [Fact]
    public void Write_SixteenHeaps_LogsEveryOne()
    {
        var heaps = new (ulong, VkMemoryHeapFlags)[16];
        var budgets = new VmaBudget[16];
        for (int i = 0; i < heaps.Length; i++)
        {
            heaps[i] = ((ulong)(i + 1) << 20, 0);
            budgets[i] = Budget((ulong)i, (ulong)(i + 1) << 19);
        }

        ValidationLog sink = new();
        (Logger logger, LogCategory log) = ValidationLog.Create(sink);
        using (logger)
        {
            HeapBudgetLog.Write(Memory(heaps), budgets, log);
        }

        Assert.Equal(16, sink.Count);
        Assert.Equal("Out of GPU memory: heap 15 (host, 16777216 bytes) has 15 bytes in use of a 8388608-byte budget", sink.Entries[15].Message);
    }

    [Fact]
    public void Write_NoHeaps_LogsNothing()
    {
        ValidationLog sink = new();
        (Logger logger, LogCategory log) = ValidationLog.Create(sink);
        using (logger)
        {
            HeapBudgetLog.Write(Memory(), [], log);
        }

        Assert.Equal(0, sink.Count);
    }

    [Theory]
    [InlineData(2, 1)]
    [InlineData(17, 16)]
    [InlineData(-1, 16)]
    public void Write_FewerBudgetsThanHeaps_Throws(int heaps, int budgets)
    {
        VkPhysicalDeviceMemoryProperties memory = Memory();
        memory.MemoryHeapCount = heaps;
        ValidationLog sink = new();
        (Logger logger, LogCategory log) = ValidationLog.Create(sink);
        using (logger)
        {
            Assert.Throws<ArgumentException>(nameof(budgets), () => HeapBudgetLog.Write(memory, new VmaBudget[budgets], log));
        }

        Assert.Equal(0, sink.Count);
    }

    [Fact]
    public void Write_NullAllocator_Throws()
    {
        Assert.Throws<ArgumentException>("vma", () => HeapBudgetLog.Write(default(VmaHandle), default));
    }

    private static VkPhysicalDeviceMemoryProperties Memory(params (ulong Size, VkMemoryHeapFlags Flags)[] heaps)
    {
        VkPhysicalDeviceMemoryProperties memory = new() { MemoryHeapCount = heaps.Length };
        for (int i = 0; i < heaps.Length; i++)
        {
            memory.MemoryHeaps[i] = new VkMemoryHeap { Size = new ByteSize64(heaps[i].Size), Flags = heaps[i].Flags };
        }

        return memory;
    }

    private static VmaBudget Budget(ulong usage, ulong budget) =>
        new() { Usage = new ByteSize((nuint)usage), Budget = new ByteSize((nuint)budget) };
}
