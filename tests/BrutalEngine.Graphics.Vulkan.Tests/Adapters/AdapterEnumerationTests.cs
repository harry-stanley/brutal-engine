using System;
using System.Collections.Generic;
using Brutal.VulkanApi;
using BrutalEngine.Core;
using BrutalEngine.Core.Logging;
using BrutalEngine.Core.Memory;
using BrutalEngine.Graphics.Backend;
using BrutalEngine.Graphics.Vulkan.Adapters;
using BrutalEngine.Graphics.Vulkan.Instances;
using BrutalEngine.Graphics.Vulkan.Tests.Loader;
using BrutalEngine.Graphics.Vulkan.Tests.Validation;
using Xunit;

namespace BrutalEngine.Graphics.Vulkan.Tests.Adapters;

public sealed class AdapterEnumerationTests
{
    // The call is passed as its name, because VulkanCall is internal and the theory is public.
    public static TheoryData<string, int, string> Faults => new()
    {
        { nameof(VulkanCall.CreateInstance), 1, "vkCreateInstance failed" },
        { nameof(VulkanCall.EnumerateDeviceExtensionProperties), 1, "vkEnumerateDeviceExtensionProperties failed" },
        { nameof(VulkanCall.EnumerateDeviceExtensionProperties), 2, "vkEnumerateDeviceExtensionProperties failed" },
        { nameof(VulkanCall.GetSurfaceSupport), 1, "vkGetPhysicalDeviceSurfaceSupportKHR failed" },
    };

    [Fact]
    public void EnumerateAdapters_NullRequest_Throws()
    {
        using Logger logger = new(new LoggerOptions { Sinks = [] });
        VulkanBackend backend = VulkanBackend.Create(Options(logger), new FakeLoaderHost { SystemEntryPoint = 0x1000 }).Value;

        Assert.Throws<ArgumentNullException>("request", () => backend.EnumerateAdapters(null!));
    }

    [Fact]
    public void RequestFrom_WithoutSurface_MapsTheInstanceAndRulesParts()
    {
        using Logger logger = new(new LoggerOptions { Sinks = [] });
        BackendDeviceRequest request = Request(logger) with { Validation = true, RequiredFeatures = GraphicsFeatures.TimestampQueries };
        ValidationAllowance[] allowList = [new(7, "tolerated in this test")];

        var mapped = AdapterEnumeration.Request.From(request, allowList);

        Assert.Equal((nameof(AdapterEnumerationTests), true), (mapped.Instance.ApplicationName, mapped.Instance.Validation));
        Assert.Equal(allowList, mapped.Instance.ValidationAllowList.ToArray());
        Assert.Equal(("Graphics.Validation", "Graphics.Vulkan"), (mapped.Instance.ValidationLog.Name, mapped.Log.Name));
        Assert.True(mapped.Instance.SurfaceExtensions.IsEmpty);
        Assert.Null(mapped.CreateSurface);
        Assert.Equal(GraphicsFeatures.TimestampQueries, mapped.RequiredFeatures);
    }

    [Fact]
    [Trait("Category", "Gpu")]
    public void EnumerateAdapters_RealDriver_ListsSuitableAdapter()
    {
        ValidationLog sink = new();
        using Logger logger = new(new LoggerOptions { Sinks = [sink] });
        VulkanBackend backend = VulkanBackend.Create(Options(logger)).Value;

        Result<IReadOnlyList<GraphicsAdapterInfo>> result = backend.EnumerateAdapters(Request(logger) with { Validation = true });

        Assert.True(result.IsSuccess, result.ToString());
        IReadOnlyList<GraphicsAdapterInfo> adapters = result.Value;
        Assert.Equal(PhysicalDeviceCount(), adapters.Count);
        for (int i = 0; i < adapters.Count; i++)
        {
            GraphicsAdapterInfo adapter = adapters[i];
            TestContext.Current.TestOutputHelper?.WriteLine(adapter.ToString());
            Assert.Equal(i, adapter.Index);
            Assert.False(string.IsNullOrWhiteSpace(adapter.Name));
            Assert.NotEqual(AdapterType.Other, adapter.Type);
            Assert.DoesNotContain("Unknown", adapter.Driver, StringComparison.Ordinal);
            Assert.Matches(@"^Vulkan 1\.[0-9]+\.[0-9]+$", adapter.ApiVersion);
            bool below13 = int.Parse(adapter.ApiVersion.Split('.')[1], System.Globalization.CultureInfo.InvariantCulture) < 3;
            Assert.Equal(below13, adapter.RejectionReasons.Count > 0 && adapter.RejectionReasons[0].EndsWith(" is below the required 1.3", StringComparison.Ordinal));
            Assert.Contains((LogLevel.Information, "Vulkan adapter: " + adapter), sink.Entries);
        }

        Assert.Contains(adapters, a => a.RejectionReasons.Count == 0);
        Assert.DoesNotContain(sink.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    [Trait("Category", "Gpu")]
    public void EnumerateAdapters_WithCompatibleSurface_ChecksPresentAndCleansUp()
    {
        ValidationLog sink = new();
        using Logger logger = new(new LoggerOptions { Sinks = [sink] });
        using SdlTestWindow window = new();
        VulkanBackend backend = VulkanBackend.Create(Options(logger)).Value;

        // Through the front end, which wraps the window in the WindowSurfaceSource the backend receives.
        Result<IReadOnlyList<GraphicsAdapterInfo>> result = GraphicsDevice.EnumerateAdapters(
            new Forwarding(backend),
            new GraphicsDeviceOptions { Logger = logger, CompatibleWindow = window, Validation = true, ApplicationName = nameof(AdapterEnumerationTests) });

        Assert.True(result.IsSuccess, result.ToString());
        Assert.Equal(1, window.SurfacesCreated);
        Assert.Contains(result.Value, a => a.RejectionReasons.Count == 0);

        // Each adapter is rejected for presentation exactly when the driver says none of its graphics families presents
        // to the window (on a host where one can't, such as NVIDIA with SDL's offscreen driver, that adapter is rejected).
        bool[] presents = PresentsPerDevice(window);
        Assert.Equal(presents.Length, result.Value.Count);
        foreach (GraphicsAdapterInfo adapter in result.Value)
        {
            TestContext.Current.TestOutputHelper?.WriteLine(adapter.ToString());
            Assert.Equal(!presents[adapter.Index], System.Linq.Enumerable.Contains(adapter.RejectionReasons, "no queue family supports graphics and presents to the window"));
        }

        // A surface still alive at vkDestroyInstance is a validation error, which the layer reports through the
        // messenger chained into the instance.
        Assert.DoesNotContain(sink.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Theory]
    [MemberData(nameof(Faults))]
    [Trait("Category", "Gpu")]
    public void EnumerateAdapters_InjectedFailure_FailsCleanly(string callName, int occurrence, string message)
    {
        VulkanCall call = Enum.Parse<VulkanCall>(callName);
        ValidationLog sink = new();
        using Logger logger = new(new LoggerOptions { Sinks = [sink] });
        using SdlTestWindow window = new();
        _ = VulkanBackend.Create(Options(logger)).Value;
        VulkanFaultInjector faults = new();
        faults.FailNth(call, occurrence, VkResult.ErrorOutOfHostMemory);
        InstanceFactory.DestroyTracker instances = new();
        InstanceFactory.DestroyTracker surfaces = new();

        Result<IReadOnlyList<GraphicsAdapterInfo>> result = AdapterEnumeration.Enumerate(
            new AdapterEnumeration.Request
            {
                Instance = new InstanceFactory.Options { Validation = true, ValidationLog = logger.CreateCategory("Graphics.Validation"), SurfaceExtensions = window.ExtensionNames, InstanceDestroys = instances },
                CreateSurface = window.CreateVulkanSurface,
                SurfaceDestroys = surfaces,
            },
            new VulkanCallGate { Faults = faults });

        Assert.Equal(new EngineError(ErrorKind.OutOfMemory, message, (int)VkResult.ErrorOutOfHostMemory), result.Error);
        Assert.False(faults.TryTake(call, out _));
        int created = call == VulkanCall.CreateInstance ? 0 : 1;
        Assert.Equal((created, created, created), (instances.Destroyed, window.SurfacesCreated, surfaces.Destroyed));
        Assert.DoesNotContain(sink.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    [Trait("Category", "Gpu")]
    public void EnumerateAdapters_SurfaceCreationFails_DestroysTheInstance()
    {
        using Logger logger = new(new LoggerOptions { Sinks = [] });
        _ = VulkanBackend.Create(Options(logger)).Value;
        InstanceFactory.DestroyTracker instances = new();
        InstanceFactory.DestroyTracker surfaces = new();
        EngineError failure = new(ErrorKind.Native, "test: no surface");

        Result<IReadOnlyList<GraphicsAdapterInfo>> result = AdapterEnumeration.Enumerate(
            new AdapterEnumeration.Request { Instance = new InstanceFactory.Options { InstanceDestroys = instances }, CreateSurface = _ => failure, SurfaceDestroys = surfaces },
            new VulkanCallGate());

        Assert.Equal(failure, result.Error);
        Assert.Equal((1, 0), (instances.Destroyed, surfaces.Destroyed));
    }

    private static VulkanBackendOptions Options(Logger logger) => new() { AppDirectory = AppContext.BaseDirectory, Logger = logger };

    private static BackendDeviceRequest Request(Logger logger) => new()
    {
        Logger = logger,
        Allocator = new HeapAllocator(),
        FramesInFlight = 2,
        GpuTimeout = TimeSpan.FromSeconds(10),
        ApplicationName = nameof(AdapterEnumerationTests),
    };

    // BRUTAL's own count of the instance's physical devices, independent of the reader.
    private static int PhysicalDeviceCount()
    {
        using VulkanInstance instance = InstanceFactory.Create(new InstanceFactory.Options(), new VulkanCallGate()).Value;
        return instance.Brutal.PhysicalDevices.Length;
    }

    // An oracle independent of the reader: per physical device, whether one of its graphics families presents to a fresh
    // surface of the window, asked through BRUTAL's queue family helpers and the raw table.
    private static unsafe bool[] PresentsPerDevice(SdlTestWindow window)
    {
        using VulkanInstance instance = InstanceFactory.Create(new InstanceFactory.Options { SurfaceExtensions = window.ExtensionNames }, new VulkanCallGate()).Value;
        var surface = new VkSurfaceKHR(window.CreateVulkanSurface(instance.Handle.VkHandle).Value);
        try
        {
            PhysicalDevice[] devices = instance.Brutal.PhysicalDevices;
            bool[] presents = new bool[devices.Length];
            for (int d = 0; d < devices.Length; d++)
            {
                var families = new VkQueueFamilyProperties[devices[d].GetQueueFamilyProperties_Count()];
                devices[d].GetQueueFamilyProperties_Write(families);
                for (int f = 0; f < families.Length; f++)
                {
                    Brutal.Bool32 supported = default;

                    // Sound: supported is a local, and the surface belongs to this live instance.
                    Assert.Equal(VkResult.Success, devices[d].PInvoke.vkGetPhysicalDeviceSurfaceSupportKHR.Invoke(devices[d].Handle, f, surface, &supported));
                    if ((families[f].QueueFlags & VkQueueFlags.GraphicsBit) != 0 && supported)
                    {
                        presents[d] = true;
                    }
                }
            }

            return presents;
        }
        finally
        {
            // Sound: the surface was created from this instance, which is still alive.
            instance.Brutal.PInvoke.vkDestroySurfaceKHR.Invoke(instance.Handle, surface, null);
        }
    }

    // The front end's view of the backend, until VulkanBackend implements IGraphicsBackend with CreateDevice.
    private sealed class Forwarding(VulkanBackend backend) : IGraphicsBackend
    {
        public string Name => backend.Name;

        public Result<IReadOnlyList<GraphicsAdapterInfo>> EnumerateAdapters(BackendDeviceRequest request) => backend.EnumerateAdapters(request);

        public Result<IBackendDevice> CreateDevice(BackendDeviceRequest request, int adapterIndex, BackendObjectTable objects) =>
            throw new NotSupportedException("Device creation isn't part of these tests.");
    }
}
