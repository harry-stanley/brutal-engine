using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Brutal;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Vma;
using BrutalEngine.Core;
using BrutalEngine.Core.Logging;
using BrutalEngine.Graphics.Backend;
using BrutalEngine.Graphics.Vulkan.Devices;
using BrutalEngine.Graphics.Vulkan.Instances;
using BrutalEngine.Graphics.Vulkan.Memory;
using BrutalEngine.Graphics.Vulkan.Resources;
using BrutalEngine.Graphics.Vulkan.Tests.Devices;
using BrutalEngine.Graphics.Vulkan.Tests.Memory;
using BrutalEngine.Graphics.Vulkan.Tests.Validation;
using Xunit;

namespace BrutalEngine.Graphics.Vulkan.Tests.Resources;

/// <summary>
/// The factory on real devices: every suitable adapter (on the dev host the RTX and lavapipe), each with a validated
/// instance, its own device and allocator. <see cref="BufferFactoryScriptedTests"/> has the host-independent rows.
/// </summary>
public sealed unsafe class BufferFactoryTests
{
    private const int Size = 4096;

    private static readonly BufferUsage[] _everyUsage =
    [
        BufferUsage.Vertex, BufferUsage.Index, BufferUsage.Uniform, BufferUsage.Storage, BufferUsage.Indirect,
        BufferUsage.CopySource, BufferUsage.CopyDestination,
        BufferUsage.Vertex | BufferUsage.Index | BufferUsage.Uniform | BufferUsage.Storage | BufferUsage.Indirect | BufferUsage.CopySource | BufferUsage.CopyDestination,
    ];

    [Theory]
    [Trait("Category", "Gpu")]
    [InlineData(BackendMemory.DeviceLocal)]
    [InlineData(BackendMemory.DeviceLocalHostVisiblePreferred)]
    [InlineData(BackendMemory.HostUpload)]
    [InlineData(BackendMemory.HostReadback)]
    public void Create_EachMemory_MappedPointerRule(BackendMemory memory)
    {
        bool host = memory is BackendMemory.HostUpload or BackendMemory.HostReadback;

        // A readback buffer takes only CopyDestination (docs/design/graphics.md section 5).
        BufferUsage[] usages = memory == BackendMemory.HostReadback ? [BufferUsage.CopyDestination] : _everyUsage;
        ValidationLog sink = new();

        (long errors, long warnings, int adapters) = OnEveryAdapter(sink, gpu =>
        {
            BufferFactory factory = new(gpu.Core, gpu.Vma, gpu.Gate, default);
            foreach (BufferUsage usage in usages)
            {
                Result<BackendBuffer> result = factory.Create(new BufferDescription { Size = Size, Usage = usage, DebugName = "mapped rule" }, memory);
                Assert.True(result.IsSuccess, $"{gpu.Name} {usage}: {result}");
                BackendBuffer buffer = result.Value;
                Assert.True(factory.Table.TryGet(buffer.Id, out BufferFactory.Entry entry));

                // Where VMA placed it, read back from VMA: values only a real allocation has.
                gpu.Vma.GetAllocationInfo(entry.Allocation, out VmaAllocationInfo info);
                gpu.Vma.GetMemoryTypeProperties(info.MemoryType, out VkMemoryPropertyFlags flags);
                Assert.True(info.Size.Value >= Size, $"{gpu.Name}: allocation of {info.Size.Value} bytes");
                Assert.False(info.DeviceMemory.IsNull());
                if (host)
                {
                    Assert.NotEqual(0, buffer.MappedPointer);
                    Assert.Equal((nint)info.PMappedData.Pointer, buffer.MappedPointer);
                    VkMemoryPropertyFlags coherent = VkMemoryPropertyFlags.HostVisibleBit | VkMemoryPropertyFlags.HostCoherentBit;
                    Assert.Equal(coherent, flags & coherent);
                    Write(buffer.MappedPointer, seed: (byte)usage);

                    // Still valid after other buffers come and go: only Destroy ends the mapping.
                    ulong other = factory.Create(new BufferDescription { Size = Size, Usage = usage }, memory).Value.Id;
                    Assert.True(factory.Destroy(other));
                    Assert.True(ReadsBack(buffer.MappedPointer, seed: (byte)usage), $"{gpu.Name} {usage}: the mapping changed");
                }
                else
                {
                    Assert.Equal(0, buffer.MappedPointer);
                }

                Assert.True(factory.Destroy(buffer.Id));
                Assert.False(factory.Table.TryGet(buffer.Id, out _));
                Assert.False(factory.Destroy(buffer.Id));
                TestContext.Current.TestOutputHelper?.WriteLine($"{gpu.Name} {memory} {usage}: memory type {info.MemoryType} flags 0x{(uint)flags:x}");
            }

            Assert.Equal(0, factory.Table.Count);
        });

        // A buffer left alive would be reported at vkDestroyDevice.
        Assert.Equal((0L, 0L), (errors, warnings));
        Assert.True(adapters > 0);
        Assert.DoesNotContain(sink.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    [Trait("Category", "Gpu")]
    public void Create_AppliesNameOnce()
    {
        ValidationLog sink = new();
        (long errors, _, _) = OnEveryAdapter(sink, gpu =>
        {
            Assert.True(gpu.Core.Naming.Enabled, $"{gpu.Name}: a validated instance names objects");
            RecordingBuffers native = new() { Inner = new BufferFactory.NativeBuffers(gpu.Core.Device, gpu.Vma) };
            BufferFactory factory = new(native, gpu.Core.Naming, gpu.Gate, new ObjectTable<BufferFactory.Entry>(), default);

            ulong named = factory.Create(new BufferDescription { Size = Size, Usage = BufferUsage.Vertex, DebugName = "sprite vertices" }, BackendMemory.DeviceLocal).Value.Id;
            (VkBuffer buffer, VmaAllocation allocation) = native.LastCreated;
            ulong unnamed = factory.Create(new BufferDescription { Size = Size, Usage = BufferUsage.Vertex }, BackendMemory.HostUpload).Value.Id;

            // Once each, for the named buffer only, through the real calls, which succeeded.
            Assert.Equal([(VkObjectType.Buffer, (ulong)buffer.VkHandle, "sprite vertices")], native.ObjectNames);
            Assert.Equal([(allocation, "sprite vertices")], native.AllocationNames);
            gpu.Vma.GetAllocationInfo(allocation, out VmaAllocationInfo info);
            Assert.Equal("sprite vertices", RecordingBuffers.Read(info.PName.Pointer));

            Assert.True(factory.Destroy(named));
            Assert.True(factory.Destroy(unnamed));
            Assert.Single(native.ObjectNames);
            Assert.Single(native.AllocationNames);
        });

        Assert.Equal(0L, errors);
        Assert.DoesNotContain(sink.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    [Trait("Category", "Gpu")]
    public void Create_Name_ReachesValidationMessages()
    {
        // A deliberate error on the buffer (a fill at an offset that isn't a multiple of 4, recorded and never
        // submitted) makes the layer report it, naming the objects involved with the names it was given.
        ValidationLog sink = new();
        (long errors, _, int adapters) = OnEveryAdapter(sink, gpu =>
        {
            BufferFactory factory = new(gpu.Core, gpu.Vma, gpu.Gate, default);
            ulong id = factory.Create(new BufferDescription { Size = Size, Usage = BufferUsage.CopyDestination, DebugName = "named for validation" }, BackendMemory.DeviceLocal).Value.Id;
            Assert.True(factory.Table.TryGet(id, out BufferFactory.Entry entry));
            using ObjectNameMessenger names = new(gpu.Instance);

            RecordMisalignedFill(gpu.Core, entry.Buffer);

            Assert.Contains((VkObjectType.Buffer, (ulong)entry.Buffer.VkHandle, "named for validation"), names.Objects);
            Assert.True(factory.Destroy(id));
        });

        // The fill is the only error, once per adapter.
        Assert.Equal(adapters, errors);
        Assert.Contains(sink.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("VUID-vkCmdFillBuffer-dstOffset", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Gpu")]
    public void Create_InjectedOutOfMemory_LogsBudgets()
    {
        ValidationLog sink = new();
        ValidationLog budgets = new();
        (Logger budgetLogger, _) = ValidationLog.Create(budgets);
        using (budgetLogger)
        {
            LogCategory log = budgetLogger.CreateCategory("Graphics.Vulkan");
            (long errors, _, _) = OnEveryAdapter(sink, gpu =>
            {
                // The heaps from the adapter's own query, not from VMA's copy, which the code under test reads.
                PhysicalDevice adapter = gpu.Core.Device.PhysicalDevice;
                VkPhysicalDeviceMemoryProperties memory = default;
                adapter.PInvoke.vkGetPhysicalDeviceMemoryProperties.Invoke(adapter.Handle, &memory);
                Assert.InRange(memory.MemoryHeapCount, 1, 16);
                VulkanFaultInjector faults = new();
                VulkanCallGate gate = new() { Faults = faults };
                BufferFactory factory = new(gpu.Core, gpu.Vma, gate, log);
                BufferDescription description = new() { Size = Size, Usage = BufferUsage.Uniform, DebugName = "exhausted" };
                faults.FailNth(VulkanCall.CreateBuffer, 1, VkResult.ErrorOutOfDeviceMemory);
                int before = budgets.Entries.Count;

                Result<BackendBuffer> exhausted = factory.Create(description, BackendMemory.DeviceLocal);

                Assert.Equal(new EngineError(ErrorKind.OutOfMemory, "vmaCreateBuffer failed", (int)VkResult.ErrorOutOfDeviceMemory), exhausted.Error);
                var lines = budgets.Entries.Skip(before).ToList();
                Assert.Equal(memory.MemoryHeapCount, lines.Count);
                for (int heap = 0; heap < lines.Count; heap++)
                {
                    Assert.Equal(LogLevel.Warning, lines[heap].Level);
                    Match line = Regex.Match(lines[heap].Message, @"^Out of GPU memory: heap (\d+) \((device-local|host), (\d+) bytes\) has (\d+) bytes in use of a (\d+)-byte budget$");
                    Assert.True(line.Success, lines[heap].Message);
                    VkMemoryHeap expected = memory.MemoryHeaps[heap];
                    Assert.Equal(heap, int.Parse(line.Groups[1].Value, CultureInfo.InvariantCulture));
                    Assert.Equal((expected.Flags & VkMemoryHeapFlags.DeviceLocalBit) != 0 ? "device-local" : "host", line.Groups[2].Value);
                    Assert.Equal(expected.Size.Value, ulong.Parse(line.Groups[3].Value, CultureInfo.InvariantCulture));
                    Assert.True(ulong.Parse(line.Groups[5].Value, CultureInfo.InvariantCulture) > 0, $"{gpu.Name}: heap {heap} has a budget");
                    TestContext.Current.TestOutputHelper?.WriteLine($"{gpu.Name}: {lines[heap].Message}");
                }

                // A lost device is Unavailable and logs no budgets.
                faults.FailNth(VulkanCall.CreateBuffer, 1, VkResult.ErrorDeviceLost);
                Result<BackendBuffer> lost = factory.Create(description, BackendMemory.HostUpload);

                Assert.Equal(new EngineError(ErrorKind.Unavailable, "vmaCreateBuffer failed", (int)VkResult.ErrorDeviceLost), lost.Error);
                Assert.True(gate.IsLost);
                Assert.Equal(before + lines.Count, budgets.Entries.Count);
                Assert.Equal(0, factory.Table.Count);
            });

            Assert.Equal(0L, errors);
        }
    }

    [Fact]
    [Trait("Category", "Gpu")]
    public void CreateDestroy_1000_DoesNotAllocate()
    {
        // Every memory kind's path, named, in turn; warmed once each so the table has grown and every path is jitted.
        BackendMemory[] kinds = [BackendMemory.DeviceLocal, BackendMemory.DeviceLocalHostVisiblePreferred, BackendMemory.HostUpload, BackendMemory.HostReadback];
        BufferDescription description = new() { Size = 256, Usage = BufferUsage.CopyDestination, DebugName = "steady" };
        ValidationLog sink = new() { Record = false };

        (long errors, _, _) = OnEveryAdapter(sink, gpu =>
        {
            BufferFactory factory = new(gpu.Core, gpu.Vma, gpu.Gate, default);
            long checksum = Cycle(factory, description, kinds, kinds.Length);

            long before = GC.GetAllocatedBytesForCurrentThread();
            checksum += Cycle(factory, description, kinds, 1000);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.Equal(0, allocated);
            Assert.Equal(1004, checksum);
            Assert.Equal(0, factory.Table.Count);
        });

        Assert.Equal(0L, errors);
        Assert.Equal(0, sink.Count);
    }

    [Fact]
    [Trait("Category", "Gpu")]
    public void Constructor_BadDeviceOrAllocator_Throws()
    {
        ValidationLog sink = new();
        _ = OnEveryAdapter(sink, gpu =>
        {
            Assert.Throws<ArgumentNullException>("gate", () => new BufferFactory(gpu.Core, gpu.Vma, null!, default));
            Assert.Throws<ArgumentException>("vma", () => new BufferFactory(gpu.Core, default, gpu.Gate, default));
        });

        using Logger logger = new(new LoggerOptions { Sinks = [] });
        using VulkanInstance instance = GpuDevices.CreateInstance(logger.CreateCategory("Graphics.Validation"), []);
        VulkanDeviceCore core = GpuAllocators.CreateDevice(instance, GpuDevices.Suitable(instance)[0], withoutBudget: false);
        core.Dispose();
        Assert.Throws<ObjectDisposedException>(() => new BufferFactory(core, new VmaHandle(new VmaAllocatorHandle(0x5150)), new VulkanCallGate(), default));
    }

    // Creates and destroys count buffers, cycling through the kinds; counts the successes.
    private static long Cycle(BufferFactory factory, in BufferDescription description, BackendMemory[] kinds, int count)
    {
        long done = 0;
        for (int i = 0; i < count; i++)
        {
            Result<BackendBuffer> result = factory.Create(description, kinds[i % kinds.Length]);
            if (result.TryGetValue(out BackendBuffer buffer) && factory.Destroy(buffer.Id))
            {
                done++;
            }
        }

        return done;
    }

    private static void Write(nint mapped, byte seed)
    {
        Span<byte> bytes = new((void*)mapped, Size);
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)(seed + (i * 7));
        }
    }

    private static bool ReadsBack(nint mapped, byte seed)
    {
        ReadOnlySpan<byte> bytes = new((void*)mapped, Size);
        for (int i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] != (byte)(seed + (i * 7)))
            {
                return false;
            }
        }

        return true;
    }

    // Records vkCmdFillBuffer at offset 1 into a command buffer that is never submitted, then frees it.
    private static void RecordMisalignedFill(VulkanDeviceCore core, VkBuffer buffer)
    {
        Device device = core.Device;
        VkCommandPool pool = device.CreateCommandPool(new VkCommandPoolCreateInfo { QueueFamilyIndex = core.QueueFamily }, null);
        try
        {
            VkCommandBufferAllocateInfo allocate = new() { CommandPool = pool, Level = VkCommandBufferLevel.Primary };

            // BRUTAL makes commandBufferCount internal (its wrapper sets it); write the field at its explicit offset, 28
            // (Brutal.Vulkan/Generated/Structs/VkCommandBufferAllocateInfo.gen.cs:51-52). Sound: inside the local struct.
            *(int*)((byte*)&allocate + 28) = 1;
            VkCommandBuffer commands = default;
            Assert.Equal(VkResult.Success, device.PInvoke.vkAllocateCommandBuffers.Invoke(device.Handle, &allocate, &commands));
            VkCommandBufferBeginInfo begin = new() { Flags = VkCommandBufferUsageFlags.OneTimeSubmitBit };
            Assert.Equal(VkResult.Success, device.PInvoke.vkBeginCommandBuffer.Invoke(commands, &begin));
            device.PInvoke.vkCmdFillBuffer.Invoke(commands, buffer, new ByteSize64(1), new ByteSize64(4), 0);
            Assert.Equal(VkResult.Success, device.PInvoke.vkEndCommandBuffer.Invoke(commands));
        }
        finally
        {
            device.DestroyCommandPool(pool, null);
        }
    }

    // Runs body on every suitable adapter with its own device, gate and allocator, then returns the instance's
    // validation counts (read after the instance is gone, so a leak at vkDestroyDevice counts) and the adapter count.
    private static (long Errors, long Warnings, int Adapters) OnEveryAdapter(ValidationLog sink, Action<Gpu> body)
    {
        (Logger logger, LogCategory category) = ValidationLog.Create(sink);
        using (logger)
        {
            VulkanInstance instance = GpuDevices.CreateInstance(category, []);
            int count;
            try
            {
                List<GpuDevices.Adapter> adapters = GpuDevices.Suitable(instance);
                Assert.NotEmpty(adapters);
                count = adapters.Count;
                foreach (GpuDevices.Adapter adapter in adapters)
                {
                    using VulkanDeviceCore core = GpuAllocators.CreateDevice(instance, adapter, withoutBudget: false);
                    VulkanCallGate gate = new();
                    Result<VmaHandle> vma = VmaAllocatorFactory.Create(core, gate);
                    Assert.True(vma.IsSuccess, vma.ToString());
                    try
                    {
                        body(new Gpu(adapter.Candidate.Name, instance, core, vma.Value, gate));
                    }
                    finally
                    {
                        vma.Value.Destroy();
                    }
                }
            }
            finally
            {
                instance.Dispose();
            }

            return (instance.ValidationErrors, instance.ValidationWarnings, count);
        }
    }

    private readonly record struct Gpu(string Name, VulkanInstance Instance, VulkanDeviceCore Core, VmaHandle Vma, VulkanCallGate Gate);
}
