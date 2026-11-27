using System;
using System.Collections.Generic;
using Brutal.Pointers;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Vma;
using BrutalEngine.Core;
using BrutalEngine.Core.Logging;
using BrutalEngine.Graphics.Vulkan.Devices;
using BrutalEngine.Graphics.Vulkan.Instances;
using BrutalEngine.Graphics.Vulkan.Memory;
using BrutalEngine.Graphics.Vulkan.Tests.Devices;
using BrutalEngine.Graphics.Vulkan.Tests.Validation;
using Xunit;

namespace BrutalEngine.Graphics.Vulkan.Tests.Memory;

public sealed unsafe class VmaAllocatorTests
{
    // Literals from BRUTAL's generated bindings: VMA_ALLOCATOR_CREATE_EXT_MEMORY_BUDGET_BIT
    // (Brutal.Vulkan.Vma/Generated/Types/Enums/VmaAllocatorCreateFlags.gen.cs:10) and VK_API_VERSION_1_3, which is
    // VK_MAKE_API_VERSION(0, 1, 3, 0) = (1 << 22) | (3 << 12).
    private const uint BudgetBit = 0x00000008;
    private const uint ApiVersion13 = 0x00403000;

    // VMA's result, then the kind section 6 gives it: out of memory, a lost device, anything else Native.
    public static TheoryData<VkResult, ErrorKind, bool> Failures => new()
    {
        { VkResult.ErrorOutOfHostMemory, ErrorKind.OutOfMemory, false },
        { VkResult.ErrorOutOfDeviceMemory, ErrorKind.OutOfMemory, false },
        { VkResult.ErrorDeviceLost, ErrorKind.Unavailable, true },
        { VkResult.ErrorInitializationFailed, ErrorKind.Native, false },
        { VkResult.ErrorUnknown, ErrorKind.Native, false },
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Create_Request_FillsTheDescription(bool memoryBudget)
    {
        RecordingVma native = new();
        VulkanCallGate gate = new();

        Result<VmaHandle> result = VmaAllocatorFactory.Create(native, Request(memoryBudget), gate);

        Assert.True(result.IsSuccess, result.ToString());
        Assert.Equal(RecordingVma.ScriptedHandle, result.Value.VkHandle.VkHandle);
        Assert.False(result.Value.IsNull);
        Assert.Equal(1, native.Calls);
        VmaAllocatorCreateInfo info = native.Info;
        Assert.Equal(memoryBudget ? BudgetBit : 0u, (uint)info.Flags);
        Assert.Equal((0x2200, 0x3300, 0x1100), (info.PhysicalDevice.VkHandle, info.Device.VkHandle, info.Instance.VkHandle));
        Assert.Equal(ApiVersion13, info.VulkanApiVersion);
        Assert.Equal(0u, (uint)info.PreferredLargeHeapBlockSize.Value);
        Assert.True(info.PAllocationCallbacks.IsNull());
        Assert.True(info.PDeviceMemoryCallbacks.IsNull());
        Assert.True(info.PHeapSizeLimit.IsNull());
        Assert.True(info.PTypeExternalMemoryHandleTypes.IsNull());

        // Only the two entry points VMA fetches the rest from; every other function stays null.
        Assert.True(native.Functions.HasValue, "the description has functions");
        VmaVulkanFunctions functions = native.Functions.Value;
        Assert.Equal(0x4400, (nint)functions.VkGetInstanceProcAddr.Invoke);
        Assert.Equal(0x5500, (nint)functions.VkGetDeviceProcAddr.Invoke);
        ReadOnlySpan<byte> rest = new ReadOnlySpan<byte>(&functions, sizeof(VmaVulkanFunctions))[16..];
        Assert.Equal(-1, rest.IndexOfAnyExcept((byte)0));
        Assert.False(gate.IsLost);
    }

    [Theory]
    [MemberData(nameof(Failures))]
    public void Create_NativeFailure_ReturnsTheGatesError(VkResult code, ErrorKind kind, bool lost)
    {
        RecordingVma native = new() { Result = code };
        VulkanCallGate gate = new();

        Result<VmaHandle> result = VmaAllocatorFactory.Create(native, Request(true), gate);

        Assert.Equal(new EngineError(kind, "vmaCreateAllocator failed", (int)code), result.Error);
        Assert.Equal(1, native.Calls);
        Assert.Equal(lost, gate.IsLost);
    }

    // A scripted source, so the occurrence counts this factory's calls on every host: occurrence 1 makes no native
    // call, occurrence 2 exactly one, the first call's.
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Create_InjectedFault_SkipsVma(int occurrence)
    {
        VulkanFaultInjector faults = new();
        faults.FailNth(VulkanCall.CreateVmaAllocator, occurrence, VkResult.ErrorOutOfHostMemory);
        RecordingVma native = new();
        VulkanCallGate gate = new() { Faults = faults };
        for (int i = 1; i < occurrence; i++)
        {
            Assert.True(VmaAllocatorFactory.Create(native, Request(false), gate).IsSuccess);
        }

        Result<VmaHandle> result = VmaAllocatorFactory.Create(native, Request(false), gate);

        Assert.Equal(new EngineError(ErrorKind.OutOfMemory, "vmaCreateAllocator failed", (int)VkResult.ErrorOutOfHostMemory), result.Error);
        Assert.Equal(occurrence - 1, native.Calls);

        // The fault fired once: the next call reaches VMA.
        Assert.True(VmaAllocatorFactory.Create(native, Request(false), gate).IsSuccess);
        Assert.Equal(occurrence, native.Calls);
    }

    [Fact]
    public void Create_NullArguments_ThrowBeforeTheCall()
    {
        RecordingVma native = new();

        Assert.Throws<ArgumentNullException>("gate", () => VmaAllocatorFactory.Create(native, Request(false), null!));
        Assert.Throws<ArgumentNullException>("core", () => VmaAllocatorFactory.Create(native, (VulkanDeviceCore)null!, new VulkanCallGate()));
        Assert.Throws<ArgumentNullException>("core", () => VmaAllocatorFactory.Create(null!, new VulkanCallGate()));
        Assert.Equal(0, native.Calls);
    }

    [Fact]
    [Trait("Category", "Gpu")]
    public void Create_RealDevice_CreatesAndDestroys()
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
                    foreach (bool withoutBudget in (ReadOnlySpan<bool>)[false, true])
                    {
                        CreateAndDestroy(instance, adapter, withoutBudget);
                    }
                }
            }
            finally
            {
                instance.Dispose();
            }

            // A missed destroy leaves VMA's memory block alive at vkDestroyDevice, which validation reports.
            Assert.Equal((0L, 0L), (instance.ValidationErrors, instance.ValidationWarnings));
            Assert.DoesNotContain(sink.Entries, e => e.Level >= LogLevel.Warning);
        }
    }

    [Fact]
    [Trait("Category", "Gpu")]
    public void Create_InjectedFailure_ReturnsFailure()
    {
        ValidationLog sink = new();
        (Logger logger, LogCategory category) = ValidationLog.Create(sink);
        using (logger)
        {
            using VulkanInstance instance = GpuDevices.CreateInstance(category, []);
            using VulkanDeviceCore core = GpuAllocators.CreateDevice(instance, GpuDevices.Suitable(instance)[0], withoutBudget: false);
            VulkanFaultInjector faults = new();
            faults.FailNth(VulkanCall.CreateVmaAllocator, 1, VkResult.ErrorOutOfDeviceMemory);
            VulkanCallGate gate = new() { Faults = faults };
            RecordingVma native = new() { Forward = true };

            Result<VmaHandle> failed = VmaAllocatorFactory.Create(native, core, gate);

            // A regression that creates the allocator anyway must not leave it alive at vkDestroyDevice.
            if (failed.TryGetValue(out VmaHandle leaked))
            {
                leaked.Destroy();
            }

            Assert.Equal(new EngineError(ErrorKind.OutOfMemory, "vmaCreateAllocator failed", (int)VkResult.ErrorOutOfDeviceMemory), failed.Error);
            Assert.Equal(0, native.Calls);

            // The fault fired once: the same gate creates the allocator on the next call.
            Result<VmaHandle> created = VmaAllocatorFactory.Create(native, core, gate);
            Assert.True(created.IsSuccess, created.ToString());
            created.Value.Destroy();
            Assert.Equal(1, native.Calls);
            Assert.False(gate.IsLost);
        }

        Assert.DoesNotContain(sink.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    [Trait("Category", "Gpu")]
    public void Create_DisposedDevice_Throws()
    {
        using Logger logger = new(new LoggerOptions { Sinks = [] });
        using VulkanInstance instance = GpuDevices.CreateInstance(logger.CreateCategory("Graphics.Validation"), []);
        VulkanDeviceCore core = GpuAllocators.CreateDevice(instance, GpuDevices.Suitable(instance)[0], withoutBudget: false);
        core.Dispose();
        RecordingVma native = new() { Forward = true };

        Assert.Throws<ObjectDisposedException>(() => VmaAllocatorFactory.Create(native, core, new VulkanCallGate()));
        Assert.Throws<ObjectDisposedException>(() => VmaAllocatorFactory.Create(core, new VulkanCallGate()));
        Assert.Equal(0, native.Calls);
    }

    // Needs no driver, but loads VMA's native library: kept with the GPU rows, which run where the natives are set up.
    [Fact]
    [Trait("Category", "Gpu")]
    public void Destroy_NullHandle_DoesNothing()
    {
        VmaHandle none = default;

        none.Destroy();

        Assert.True(none.IsNull);
    }

    private static void CreateAndDestroy(VulkanInstance instance, GpuDevices.Adapter adapter, bool withoutBudget)
    {
        using VulkanDeviceCore core = GpuAllocators.CreateDevice(instance, adapter, withoutBudget);
        Device device = core.Device;

        // Whether the device has the extension, read from the driver's list and the device BRUTAL created, not from
        // the code under test.
        bool enabled = device.EnabledExtensions.Contains(GpuAllocators.MemoryBudget);
        Assert.Equal(!withoutBudget && adapter.Candidate.DeviceExtensions.Span.Contains(GpuAllocators.MemoryBudget), enabled);
        RecordingVma native = new() { Forward = true };

        Result<VmaHandle> result = VmaAllocatorFactory.Create(native, core, new VulkanCallGate());

        Assert.True(result.IsSuccess, $"{adapter.Candidate.Name}: {result}");
        VmaHandle allocator = result.Value;
        try
        {
            Assert.False(allocator.IsNull);
            Assert.Equal(1, native.Calls);
            Assert.Equal(enabled ? BudgetBit : 0u, (uint)native.Info.Flags);

            // VMA keeps the handles it was given, and read the heaps from the driver through the given entry points.
            allocator.GetAllocatorInfo(out VmaAllocatorInfo info);
            Assert.Equal(
                (instance.Handle.VkHandle, adapter.Device.Handle.VkHandle, device.Handle.VkHandle),
                (info.Instance.VkHandle, info.PhysicalDevice.VkHandle, info.Device.VkHandle));
            allocator.GetMemoryProperties(out Ptr<VkPhysicalDeviceMemoryProperties> memory);
            int heaps = memory.Value.MemoryHeapCount;
            Assert.InRange(heaps, 1, 16);
            Span<VmaBudget> budgets = stackalloc VmaBudget[16];
            allocator.GetHeapBudgets(budgets);
            for (int heap = 0; heap < heaps; heap++)
            {
                Assert.True(budgets[heap].Budget.Value > 0, $"{adapter.Candidate.Name}: heap {heap} has a budget");
            }

            // A buffer makes VMA allocate a device memory block, which it frees only when the allocator is destroyed.
            GpuAllocators.Buffer buffer = GpuAllocators.CreateBuffer(
                allocator,
                new VmaAllocationCreateInfo { Usage = VmaMemoryUsage.Auto, Flags = VmaAllocationCreateFlags.HostAccessSequentialWriteBit },
                1024);
            allocator.DestroyBuffer(buffer.Handle, buffer.Allocation);
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"{adapter.Candidate.Name}: budget extension {(enabled ? "on" : "off")}, flags 0x{(uint)native.Info.Flags:x}, {heaps} heap(s), heap 0 budget {budgets[0].Budget.Value} bytes");
        }
        finally
        {
            allocator.Destroy();
        }
    }

    private static VmaAllocatorFactory.Request Request(bool memoryBudget) => new(
        new VkInstance(0x1100),
        new VkPhysicalDevice(0x2200),
        new VkDevice(0x3300),
        new VkGetInstanceProcAddr((delegate* unmanaged[Cdecl]<VkInstance, byte*, PfnVkVoidFunction>)0x4400),
        new VkGetDeviceProcAddr((delegate* unmanaged[Cdecl]<VkDevice, byte*, PfnVkVoidFunction>)0x5500),
        memoryBudget);
}
