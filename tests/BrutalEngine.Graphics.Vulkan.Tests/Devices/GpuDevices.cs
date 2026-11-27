using System;
using System.Collections.Generic;
using Brutal.VulkanApi;
using BrutalEngine.Core;
using BrutalEngine.Core.Logging;
using BrutalEngine.Graphics.Vulkan.Adapters;
using BrutalEngine.Graphics.Vulkan.Devices;
using BrutalEngine.Graphics.Vulkan.Instances;
using BrutalEngine.Graphics.Vulkan.Tests.Validation;
using Xunit;

namespace BrutalEngine.Graphics.Vulkan.Tests.Devices;

/// <summary>Helpers for the <c>Gpu</c> device tests: validated instances and their suitable adapters, as enumeration judges them.</summary>
internal static class GpuDevices
{
    /// <summary>Creates a validated instance that logs to <paramref name="category"/>, with a surface's instance extensions or none.</summary>
    public static VulkanInstance CreateInstance(LogCategory category, string[] surfaceExtensions)
    {
        GpuValidation.BindLoader();
        Result<VulkanInstance> result = InstanceFactory.Create(
            new InstanceFactory.Options { ApplicationName = nameof(GpuDevices), Validation = true, ValidationLog = category, SurfaceExtensions = surfaceExtensions },
            new VulkanCallGate());
        Assert.True(result.IsSuccess, result.ToString());
        return result.Value;
    }

    /// <summary>
    /// The swapchain maintenance1 extension a device on this instance and adapter should enable, worked out from the
    /// registry's dependencies rather than the code under test: KHR with KHR surface maintenance1, else EXT with EXT.
    /// </summary>
    public static string? ExpectedMaintenance1(VulkanInstance instance, Adapter adapter)
    {
        ReadOnlySpan<string> offered = adapter.Candidate.DeviceExtensions.Span;
        if (!instance.IsExtensionEnabled("VK_KHR_surface"))
        {
            return null;
        }

        return offered.Contains("VK_KHR_swapchain_maintenance1") && instance.IsExtensionEnabled("VK_KHR_surface_maintenance1") ? "VK_KHR_swapchain_maintenance1"
            : offered.Contains("VK_EXT_swapchain_maintenance1") && instance.IsExtensionEnabled("VK_EXT_surface_maintenance1") ? "VK_EXT_swapchain_maintenance1"
            : null;
    }

    /// <summary>Every adapter the rules accept without a surface, with its chosen queue family.</summary>
    public static List<Adapter> Suitable(VulkanInstance instance)
    {
        List<Adapter> adapters = [];
        foreach (PhysicalDevice device in instance.Brutal.PhysicalDevices)
        {
            AdapterCandidate candidate = CandidateReader.Read(device, default, new VulkanCallGate()).Value;
            List<string> reasons = [];
            AdapterRules.AppendRejections(candidate, new AdapterRules.RulesRequest(GraphicsFeatures.None, HasCompatibleSurface: false), reasons);
            if (reasons.Count == 0)
            {
                adapters.Add(new Adapter(device, candidate, QueueFamilyChoice.Choose(candidate.QueueFamilies.Span, needsPresent: false)));
            }
        }

        return adapters;
    }

    /// <summary>Creates a device on an adapter with every optional feature it has and the extensions the table picks.</summary>
    public static Result<VulkanDeviceCore> Create(VulkanInstance instance, Adapter adapter, VulkanCallGate gate, InstanceFactory.DestroyTracker? destroys)
    {
        FeatureChain.NegotiatedFeatures features = FeatureChain.Negotiate(adapter.Candidate, adapter.Family, GraphicsFeatures.None, FeatureChain.Negotiable);
        DeviceExtensionSet.Selection extensions = DeviceExtensionSet.Choose(Offered(adapter), instance.EnabledExtensions);
        return VulkanDeviceCore.Create(instance, adapter.Device, adapter.Family, features, extensions, gate, destroys);
    }

    /// <summary>As <see cref="Create(VulkanInstance, Adapter, VulkanCallGate, InstanceFactory.DestroyTracker?)"/> through a test factory.</summary>
    public static Result<VulkanDeviceCore> Create<TFactory>(VulkanInstance instance, Adapter adapter, TFactory factory, VulkanCallGate gate, InstanceFactory.DestroyTracker? destroys)
        where TFactory : VulkanDeviceCore.IFactory
    {
        FeatureChain.NegotiatedFeatures features = FeatureChain.Negotiate(adapter.Candidate, adapter.Family, GraphicsFeatures.None, FeatureChain.Negotiable);
        DeviceExtensionSet.Selection extensions = DeviceExtensionSet.Choose(Offered(adapter), instance.EnabledExtensions);
        return VulkanDeviceCore.Create(factory, adapter.Family, features, extensions, gate, destroys);
    }

    private static HashSet<string> Offered(Adapter adapter) => new(adapter.Candidate.DeviceExtensions.ToArray(), StringComparer.Ordinal);

    /// <summary>A suitable adapter: BRUTAL's device, its candidate and the queue family a device would use.</summary>
    public readonly record struct Adapter(PhysicalDevice Device, AdapterCandidate Candidate, int Family);
}
