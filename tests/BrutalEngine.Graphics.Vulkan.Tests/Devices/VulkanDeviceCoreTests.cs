using System;
using System.Collections.Generic;
using Brutal.VulkanApi;
using BrutalEngine.Core;
using BrutalEngine.Core.Logging;
using BrutalEngine.Graphics.Vulkan.Adapters;
using BrutalEngine.Graphics.Vulkan.Devices;
using BrutalEngine.Graphics.Vulkan.Instances;
using BrutalEngine.Graphics.Vulkan.Tests.Adapters;
using BrutalEngine.Graphics.Vulkan.Tests.Validation;
using Xunit;

namespace BrutalEngine.Graphics.Vulkan.Tests.Devices;

public sealed unsafe class VulkanDeviceCoreTests
{
    private const string Swapchain = "VK_KHR_swapchain";
    private static FeatureChain.NegotiatedFeatures SomeFeatures => new(GraphicsFeatures.SamplerAnisotropy | GraphicsFeatures.TextureCompressionBC);

    // BRUTAL's result, then the kind section 13 maps it to, written from the note's table.
    public static TheoryData<VkResult, ErrorKind> Thrown => new()
    {
        { VkResult.ErrorOutOfHostMemory, ErrorKind.OutOfMemory },
        { VkResult.ErrorOutOfDeviceMemory, ErrorKind.OutOfMemory },
        { VkResult.ErrorInitializationFailed, ErrorKind.Native },
        { VkResult.ErrorExtensionNotPresent, ErrorKind.Unsupported },
        { VkResult.ErrorFeatureNotPresent, ErrorKind.Unsupported },
        { VkResult.ErrorTooManyObjects, ErrorKind.Native },
        { VkResult.ErrorDeviceLost, ErrorKind.Unavailable },
    };

    [Theory]
    [MemberData(nameof(Thrown))]
    public void Create_BrutalThrows_ConvertsToResult(VkResult code, ErrorKind kind)
    {
        string message = "Vulkan call to vkCreateDevice failed with error: " + code;
        FakeFactory factory = new() { Throws = new VkResultError(code, message) };
        InstanceFactory.DestroyTracker destroys = new();

        Result<VulkanDeviceCore> result = VulkanDeviceCore.Create(factory, 11, SomeFeatures, Selection(null), new VulkanCallGate(), destroys);

        Assert.Equal(new EngineError(kind, "vkCreateDevice failed: " + message, (int)code), result.Error);
        Assert.Equal((1, 0), (factory.Calls, destroys.Destroyed));
    }

    [Theory]
    [InlineData(VkResult.ErrorOutOfHostMemory, ErrorKind.OutOfMemory)]
    [InlineData(VkResult.ErrorInitializationFailed, ErrorKind.Native)]
    public void Create_InjectedFault_ReturnsBeforeBrutal(VkResult code, ErrorKind kind)
    {
        VulkanFaultInjector faults = new();
        faults.FailNth(VulkanCall.CreateDevice, 1, code);
        FakeFactory factory = new() { Throws = new VkResultError(VkResult.ErrorUnknown, "BRUTAL was called") };

        Result<VulkanDeviceCore> result = VulkanDeviceCore.Create(factory, 11, SomeFeatures, Selection(null), new VulkanCallGate { Faults = faults }, null);

        Assert.Equal(new EngineError(kind, "vkCreateDevice failed", (int)code), result.Error);
        Assert.Equal(0, factory.Calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("VK_KHR_swapchain_maintenance1")]
    [InlineData("VK_EXT_swapchain_maintenance1")]
    public void Create_PassesOneQueueTheExtensionsAndTheChain(string? maintenance1)
    {
        DeviceExtensionSet.Selection selection = Selection(maintenance1);
        bool checkedInfo = false;
        FakeFactory factory = new()
        {
            Throws = new VkResultError(VkResult.ErrorInitializationFailed, "test"),
            Inspect = info =>
            {
                // Inside the call, while the description's pointers are valid.
                Assert.Equal(1, info.QueueCreateInfos.Length);
                VkDeviceQueueCreateInfo queue = info.QueueCreateInfos[0];
                Assert.Equal((11, 1, VkDeviceQueueCreateFlags.None), (queue.QueueFamilyIndex, queue.QueueCount, queue.Flags));
                Assert.True(queue.Next is null);
                Assert.Equal(1.0f, *queue.QueuePriorities);
                Assert.True(info.Extensions.SetEquals(selection.Extensions));
                Assert.Same(StringComparer.Ordinal, info.Extensions.Comparer);
                Assert.True(info.Features.IsNull(), "pEnabledFeatures must be null with VkPhysicalDeviceFeatures2 chained");
                Assert.Null(info.Allocator);
                FeatureChainTests.AssertChain((VkPhysicalDeviceFeatures2*)info.Next.Pointer, SomeFeatures.Enabled, maintenance1 is not null);
                checkedInfo = true;
            },
        };

        Result<VulkanDeviceCore> result = VulkanDeviceCore.Create(factory, 11, SomeFeatures, selection, new VulkanCallGate(), null);

        Assert.True(result.IsFailure);
        Assert.True(checkedInfo);
    }

    [Fact]
    public void Create_Misuse_ThrowsBeforeTheCall()
    {
        FakeFactory factory = new();
        VulkanCallGate gate = new();

        Assert.Throws<ArgumentOutOfRangeException>("queueFamily", () => VulkanDeviceCore.Create(factory, -1, SomeFeatures, Selection(null), gate, null));
        Assert.Throws<ArgumentNullException>("extensions", () => VulkanDeviceCore.Create(factory, 0, SomeFeatures, default, gate, null));
        Assert.Throws<ArgumentNullException>("gate", () => VulkanDeviceCore.Create(factory, 0, SomeFeatures, Selection(null), null!, null));
        Assert.Throws<ArgumentNullException>("instance", () => VulkanDeviceCore.Create(null!, null!, 0, SomeFeatures, Selection(null), gate));
        Assert.Equal(0, factory.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Gpu")]
    public void Create_RealAdapter_CreatesDeviceAndQueue(bool withSurfaceExtensions)
    {
        ValidationLog sink = new();
        (Logger logger, LogCategory category) = ValidationLog.Create(sink);
        using (logger)
        {
            using SdlTestWindow? window = withSurfaceExtensions ? new SdlTestWindow() : null;
            VulkanInstance instance = GpuDevices.CreateInstance(category, window?.ExtensionNames ?? []);
            try
            {
                List<GpuDevices.Adapter> adapters = GpuDevices.Suitable(instance);
                Assert.NotEmpty(adapters);
                foreach (GpuDevices.Adapter adapter in adapters)
                {
                    using VulkanDeviceCore core = GpuDevices.Create(instance, adapter, new VulkanCallGate(), null).Value;
                    Device device = core.Device;
                    VkQueue queue = default;
                    device.PInvoke.vkGetDeviceQueue.Invoke(device.Handle, adapter.Family, 0, &queue);

                    Assert.False(device.IsNull());
                    Assert.Equal(adapter.Family, core.QueueFamily);
                    Assert.False(core.Queue.IsNull());
                    Assert.Equal(queue.VkHandle, core.Queue.VkHandle);
                    Assert.Equal(VkResult.Success, device.PInvoke.vkQueueWaitIdle.Invoke(core.Queue));
                    Assert.True(device.EnabledExtensions.SetEquals(core.Extensions.Extensions));
                    Assert.Equal(withSurfaceExtensions, device.EnabledExtensions.Contains(Swapchain));
                    Assert.Equal(GpuDevices.ExpectedMaintenance1(instance, adapter), core.Extensions.SwapchainMaintenance1);
                    TestContext.Current.TestOutputHelper?.WriteLine(
                        $"{adapter.Candidate.Name}: family {core.QueueFamily}, features {core.Features.Enabled}, extensions [{string.Join(", ", core.Extensions.Extensions)}], instance [{string.Join(", ", instance.EnabledExtensions.ToArray())}]");
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

    [Fact]
    [Trait("Category", "Gpu")]
    public void Create_InjectedFailure_ReturnsFailureWithoutDevice()
    {
        ValidationLog sink = new();
        (Logger logger, LogCategory category) = ValidationLog.Create(sink);
        using (logger)
        {
            using VulkanInstance instance = GpuDevices.CreateInstance(category, []);
            GpuDevices.Adapter adapter = GpuDevices.Suitable(instance)[0];
            VulkanFaultInjector faults = new();
            faults.FailNth(VulkanCall.CreateDevice, 1, VkResult.ErrorOutOfDeviceMemory);
            VulkanCallGate gate = new() { Faults = faults };
            FakeFactory factory = new() { Adapter = adapter.Device };
            InstanceFactory.DestroyTracker destroys = new();

            Result<VulkanDeviceCore> failed = GpuDevices.Create(instance, adapter, factory, gate, destroys);

            // A regression that creates the device anyway must not leave it alive at vkDestroyInstance.
            if (failed.TryGetValue(out VulkanDeviceCore? leaked))
            {
                leaked.Dispose();
            }

            Assert.Equal(new EngineError(ErrorKind.OutOfMemory, "vkCreateDevice failed", (int)VkResult.ErrorOutOfDeviceMemory), failed.Error);
            Assert.Equal((0, 0), (factory.Calls, destroys.Destroyed));

            // The fault fired once: the same gate creates a device on the next call.
            using (VulkanDeviceCore core = GpuDevices.Create(instance, adapter, factory, gate, destroys).Value)
            {
                Assert.Equal(1, factory.Calls);
            }

            Assert.Equal(1, destroys.Destroyed);
        }

        Assert.DoesNotContain(sink.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    [Trait("Category", "Gpu")]
    public void Dispose_Twice_DestroysOnce()
    {
        ValidationLog sink = new();
        (Logger logger, LogCategory category) = ValidationLog.Create(sink);
        using (logger)
        {
            using VulkanInstance instance = GpuDevices.CreateInstance(category, []);
            InstanceFactory.DestroyTracker destroys = new();
            VulkanDeviceCore core = GpuDevices.Create(instance, GpuDevices.Suitable(instance)[0], new VulkanCallGate(), destroys).Value;

            core.Dispose();
            core.Dispose();

            Assert.Equal(1, destroys.Destroyed);
            Assert.True(core.IsDisposed);
            Assert.Throws<ObjectDisposedException>(() => core.Device);
        }

        Assert.DoesNotContain(sink.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    [Trait("Category", "Gpu")]
    public void Create_AdapterNullOrOfAnotherInstance_Throws()
    {
        using Logger logger = new(new LoggerOptions { Sinks = [] });
        using VulkanInstance instance = GpuDevices.CreateInstance(logger.CreateCategory("Graphics.Validation"), []);
        using VulkanInstance other = GpuDevices.CreateInstance(logger.CreateCategory("Graphics.Validation"), []);
        GpuDevices.Adapter foreign = GpuDevices.Suitable(other)[0];

        Assert.Throws<ArgumentNullException>("adapter", () => VulkanDeviceCore.Create(instance, null!, 0, SomeFeatures, Selection(null), new VulkanCallGate()));
        Assert.Throws<ArgumentException>("adapter", () => VulkanDeviceCore.Create(instance, foreign.Device, foreign.Family, SomeFeatures, Selection(null), new VulkanCallGate()));
    }

    [Fact]
    [Trait("Category", "Gpu")]
    public void Create_EachQueueFamily_TakesTheQueueFromThatFamily()
    {
        ValidationLog sink = new();
        (Logger logger, LogCategory category) = ValidationLog.Create(sink);
        using (logger)
        {
            using VulkanInstance instance = GpuDevices.CreateInstance(category, []);
            int created = 0;
            foreach (GpuDevices.Adapter adapter in GpuDevices.Suitable(instance))
            {
                // Every family the adapter offers, in the driver's order; any family is a valid one-queue device (each
                // has at least one queue), so none is skipped and a failure fails the test.
                foreach (AdapterCandidate.QueueFamily family in adapter.Candidate.QueueFamilies.Span)
                {
                    GpuDevices.Adapter onFamily = adapter with { Family = family.Index };
                    Result<VulkanDeviceCore> result = GpuDevices.Create(instance, onFamily, new VulkanCallGate(), null);
                    Assert.True(result.IsSuccess, $"{adapter.Candidate.Name}, family {family.Index}: {result}");
                    using VulkanDeviceCore core = result.Value;
                    Device device = core.Device;
                    VkQueue queue = default;
                    device.PInvoke.vkGetDeviceQueue.Invoke(device.Handle, family.Index, 0, &queue);

                    Assert.Equal(family.Index, core.QueueFamily);
                    Assert.False(queue.IsNull());
                    Assert.Equal(queue.VkHandle, core.Queue.VkHandle);
                    created++;
                    TestContext.Current.TestOutputHelper?.WriteLine($"{adapter.Candidate.Name}: family {family.Index} (graphics {family.Graphics}, compute {family.Compute})");
                }
            }

            Assert.True(created > 0);
        }

        Assert.DoesNotContain(sink.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    [Trait("Category", "Gpu")]
    public void Create_DisposedInstance_Throws()
    {
        using Logger logger = new(new LoggerOptions { Sinks = [] });
        VulkanInstance instance = GpuDevices.CreateInstance(logger.CreateCategory("Graphics.Validation"), []);
        GpuDevices.Adapter adapter = GpuDevices.Suitable(instance)[0];
        instance.Dispose();

        Assert.Throws<ObjectDisposedException>(() => VulkanDeviceCore.Create(instance, adapter.Device, adapter.Family, SomeFeatures, Selection(null), new VulkanCallGate()));
    }

    private static DeviceExtensionSet.Selection Selection(string? maintenance1) =>
        new(maintenance1 is null ? [Swapchain] : [Swapchain, maintenance1], false, maintenance1, false);

    /// <summary>
    /// Observes BRUTAL's call: counts it, inspects the description while its pointers are valid, then throws or creates
    /// the device on a real adapter.
    /// </summary>
    internal sealed class FakeFactory : VulkanDeviceCore.IFactory
    {
        public delegate void InspectInfo(Device.CreateInfo info);

        public VkResultError? Throws { get; init; }

        public InspectInfo? Inspect { get; init; }

        public PhysicalDevice? Adapter { get; init; }

        public int Calls { get; private set; }

        public Device CreateDevice(Device.CreateInfo info)
        {
            Calls++;
            Inspect?.Invoke(info);
            if (Throws is not null)
            {
                throw Throws;
            }

            Assert.NotNull(Adapter);
            return new VulkanDeviceCore.AdapterFactory(Adapter).CreateDevice(info);
        }
    }
}
