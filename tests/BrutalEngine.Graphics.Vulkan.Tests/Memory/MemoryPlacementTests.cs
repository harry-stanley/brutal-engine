using System;
using System.Collections.Generic;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Vma;
using BrutalEngine.Core.Logging;
using BrutalEngine.Graphics.Backend;
using BrutalEngine.Graphics.Vulkan.Devices;
using BrutalEngine.Graphics.Vulkan.Instances;
using BrutalEngine.Graphics.Vulkan.Memory;
using BrutalEngine.Graphics.Vulkan.Tests.Devices;
using BrutalEngine.Graphics.Vulkan.Tests.Validation;
using Xunit;

namespace BrutalEngine.Graphics.Vulkan.Tests.Memory;

public sealed class MemoryPlacementTests
{
    // VkMemoryPropertyFlagBits (the Vulkan specification): DEVICE_LOCAL 0x1, HOST_VISIBLE 0x2, HOST_COHERENT 0x4,
    // HOST_CACHED 0x8.
    private const uint DeviceLocal = 0x1;
    private const uint HostVisible = 0x2;
    private const uint HostCoherent = 0x4;

    // Section 6's table, as literals from BRUTAL's generated bindings: VmaMemoryUsage AUTO 7, AUTO_PREFER_DEVICE 8
    // (Brutal.Vulkan.Vma/Generated/Types/Enums/VmaMemoryUsage.gen.cs:12-13); VmaAllocationCreateFlags MAPPED 0x4,
    // WITHIN_BUDGET 0x100, HOST_ACCESS_SEQUENTIAL_WRITE 0x400, HOST_ACCESS_RANDOM 0x800
    // (Generated/Types/Enums/VmaAllocationCreateFlags.gen.cs:10, :14, :16-17). Columns: usage, flags, required, preferred.
    public static TheoryData<BackendMemory, uint, uint, uint, uint> Table => new()
    {
        { BackendMemory.DeviceLocal, 8, 0x100, 0, 0 },
        { BackendMemory.DeviceLocalHostVisiblePreferred, 8, 0x100, 0, 0x2 },
        { BackendMemory.HostUpload, 7, 0x400 | 0x4 | 0x100, 0x2 | 0x4, 0 },
        { BackendMemory.HostReadback, 7, 0x800 | 0x4 | 0x100, 0x2 | 0x4, 0x8 },
    };

    [Theory]
    [MemberData(nameof(Table))]
    public unsafe void For_EachMemory_MatchesTable(BackendMemory memory, uint usage, uint flags, uint required, uint preferred)
    {
        VmaAllocationCreateInfo info = MemoryPlacement.For(memory);

        Assert.Equal((usage, flags, required, preferred), ((uint)info.Usage, (uint)info.Flags, (uint)info.RequiredFlags, (uint)info.PreferredFlags));

        // Every other field is zero: any memory type, the default pool, no user data, priority or alignment.
        Assert.Equal(0u, info.MemoryTypeBits);
        Assert.True(info.Pool.IsNull());
        Assert.True(info.PUserData.IsNull());
        Assert.Equal(0f, info.Priority);
        Assert.Equal(0u, (uint)info.MinAlignment.Value);
    }

    [Fact]
    public void Table_CoversEveryMemory()
    {
        List<BackendMemory> rows = [];
        foreach (TheoryDataRow<BackendMemory, uint, uint, uint, uint> row in Table)
        {
            rows.Add(row.Data.Item1);
        }

        Assert.Equal(Enum.GetValues<BackendMemory>(), rows);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void For_UndefinedMemory_Throws(int value) =>
        Assert.Throws<ArgumentOutOfRangeException>("memory", () => MemoryPlacement.For((BackendMemory)value));

    [Fact]
    public void For_AllocatesNothing()
    {
        uint checksum = Fold();

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
        {
            checksum += Fold();
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.NotEqual(0u, checksum);

        // Every memory kind, folded into one number so no call is optimised away.
        static uint Fold()
        {
            uint sum = 0;
            for (BackendMemory memory = BackendMemory.DeviceLocal; memory <= BackendMemory.HostReadback; memory++)
            {
                VmaAllocationCreateInfo info = MemoryPlacement.For(memory);
                sum += (uint)info.Usage + (uint)info.Flags + (uint)info.RequiredFlags + (uint)info.PreferredFlags;
            }

            return sum;
        }
    }

    [Fact]
    [Trait("Category", "Gpu")]
    public void HostMemories_AreCoherentOnRealDevice()
    {
        ForEachAllocator((adapter, allocator) =>
        {
            foreach (BackendMemory memory in (ReadOnlySpan<BackendMemory>)[BackendMemory.HostUpload, BackendMemory.HostReadback])
            {
                const int Size = 4096;
                GpuAllocators.Buffer buffer = GpuAllocators.CreateBuffer(allocator, MemoryPlacement.For(memory), Size);
                try
                {
                    uint flags = (uint)buffer.MemoryFlags;
                    Assert.True((flags & (HostVisible | HostCoherent)) == (HostVisible | HostCoherent), $"{adapter.Candidate.Name}: {memory} in memory type {buffer.Info.MemoryType} (0x{flags:x})");
                    Assert.True(buffer.Info.Size.Value >= Size);
                    GpuAllocators.WriteAndReadBack(buffer, Size);
                    Log(adapter, memory, buffer);
                }
                finally
                {
                    allocator.DestroyBuffer(buffer.Handle, buffer.Allocation);
                }
            }
        });
    }

    [Fact]
    [Trait("Category", "Gpu")]
    public void DeviceLocal_IsDeviceLocalOnRealDevice()
    {
        ForEachAllocator((adapter, allocator) =>
        {
            GpuAllocators.Buffer buffer = GpuAllocators.CreateBuffer(allocator, MemoryPlacement.For(BackendMemory.DeviceLocal), 4096);
            try
            {
                uint flags = (uint)buffer.MemoryFlags;
                Assert.True((flags & DeviceLocal) != 0, $"{adapter.Candidate.Name}: memory type {buffer.Info.MemoryType} (0x{flags:x})");
                Assert.True(buffer.Info.PMappedData.IsNull(), "device-local memory is never mapped");
                Log(adapter, BackendMemory.DeviceLocal, buffer);
            }
            finally
            {
                allocator.DestroyBuffer(buffer.Handle, buffer.Allocation);
            }

            // The hint is only logged: whether a host-visible device-local type exists depends on the adapter.
            GpuAllocators.Buffer hinted = GpuAllocators.CreateBuffer(allocator, MemoryPlacement.For(BackendMemory.DeviceLocalHostVisiblePreferred), 4096);
            Log(adapter, BackendMemory.DeviceLocalHostVisiblePreferred, hinted);
            allocator.DestroyBuffer(hinted.Handle, hinted.Allocation);
        });
    }

    // Every suitable adapter with a device and an allocator, validated: the action runs once per adapter.
    private static void ForEachAllocator(Action<GpuDevices.Adapter, VmaHandle> action)
    {
        ValidationLog sink = new();
        (Logger logger, LogCategory category) = ValidationLog.Create(sink);
        using (logger)
        {
            VulkanInstance instance = GpuDevices.CreateInstance(category, []);
            try
            {
                List<GpuDevices.Adapter> adapters = GpuDevices.Suitable(instance);
                Assert.NotEmpty(adapters);
                foreach (GpuDevices.Adapter adapter in adapters)
                {
                    using VulkanDeviceCore core = GpuAllocators.CreateDevice(instance, adapter, withoutBudget: false);
                    VmaHandle allocator = VmaAllocatorFactory.Create(core, new VulkanCallGate()).Value;
                    try
                    {
                        action(adapter, allocator);
                    }
                    finally
                    {
                        allocator.Destroy();
                    }
                }
            }
            finally
            {
                instance.Dispose();
            }

            Assert.Equal((0L, 0L), (instance.ValidationErrors, instance.ValidationWarnings));
            Assert.DoesNotContain(sink.Entries, e => e.Level >= LogLevel.Warning);
        }
    }

    private static void Log(GpuDevices.Adapter adapter, BackendMemory memory, in GpuAllocators.Buffer buffer) =>
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"{adapter.Candidate.Name}: {memory} in memory type {buffer.Info.MemoryType} ({buffer.MemoryFlags})");
}
