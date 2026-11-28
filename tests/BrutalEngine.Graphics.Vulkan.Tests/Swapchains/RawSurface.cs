using System;
using System.Collections.Generic;
using System.Threading;
using Brutal.VulkanApi;
using BrutalEngine.Core;
using BrutalEngine.Core.Logging;
using BrutalEngine.Graphics.Vulkan.Adapters;
using BrutalEngine.Graphics.Vulkan.Devices;
using BrutalEngine.Graphics.Vulkan.Instances;
using BrutalEngine.Graphics.Vulkan.Tests.Adapters;
using BrutalEngine.Graphics.Vulkan.Tests.Devices;
using Xunit;

namespace BrutalEngine.Graphics.Vulkan.Tests.Swapchains;

/// <summary>
/// A raw <c>VkSurfaceKHR</c> for the swapchain components' <c>Gpu</c> tests, which come before the device facade: a
/// hidden SDL window (<see cref="SdlTestWindow"/>: headless on Linux, windowed on Windows), a validated instance with
/// the window's extensions, and the window's surface. <see cref="Presenting"/> lists every adapter whose queue family
/// can present to it, never assuming the first.
/// </summary>
/// <remarks>
/// <see cref="Dispose"/> destroys the surface, then the instance, then the window; devices made with
/// <see cref="CreateDevice"/> are the caller's and go first. The instance's validation counters stay readable afterwards.
/// </remarks>
internal sealed unsafe class RawSurface : IDisposable
{
    private readonly SdlTestWindow _window;
    private bool _disposed;

    public RawSurface(LogCategory validationLog)
    {
        _window = new SdlTestWindow();
        try
        {
            Instance = GpuDevices.CreateInstance(validationLog, _window.ExtensionNames);
            Result<nint> made;

            // SDL's surface call reaches the loader too; it takes turns with other tests' global commands.
            using (Lock.Scope scope = GlobalCommands.Gate.EnterScope())
            {
                made = _window.CreateVulkanSurface(Instance.Handle.VkHandle);
            }

            Assert.True(made.IsSuccess, made.ToString());
            Surface = new VkSurfaceKHR(made.Value);
        }
        catch
        {
            Instance?.Dispose();
            _window.Dispose();
            throw;
        }
    }

    public VulkanInstance Instance { get; }

    public VkSurfaceKHR Surface { get; }

    /// <summary>Every adapter the rules accept with this surface, with the first family that has graphics and presents to it.</summary>
    public List<GpuDevices.Adapter> Presenting()
    {
        List<GpuDevices.Adapter> adapters = [];
        foreach (PhysicalDevice device in Instance.Brutal.PhysicalDevices)
        {
            Result<AdapterCandidate> read = CandidateReader.Read(device, Surface, new VulkanCallGate());
            Assert.True(read.IsSuccess, read.ToString());
            List<string> reasons = [];
            AdapterRules.AppendRejections(read.Value, new AdapterRules.RulesRequest(GraphicsFeatures.None, HasCompatibleSurface: true), reasons);
            if (reasons.Count == 0)
            {
                adapters.Add(new GpuDevices.Adapter(device, read.Value, QueueFamilyChoice.Choose(read.Value.QueueFamilies.Span, needsPresent: true)));
            }
        }

        return adapters;
    }

    /// <summary>
    /// Creates a device on a presenting adapter, with the swapchain extension the surface's instance allows; without
    /// swapchain maintenance1 when <paramref name="withoutMaintenance1"/> is set, as on a driver that doesn't offer it.
    /// </summary>
    public VulkanDeviceCore CreateDevice(GpuDevices.Adapter adapter, VulkanCallGate gate, bool withoutMaintenance1 = false)
    {
        Result<VulkanDeviceCore> created;
        if (withoutMaintenance1)
        {
            FeatureChain.NegotiatedFeatures features = FeatureChain.Negotiate(adapter.Candidate, adapter.Family, GraphicsFeatures.None, FeatureChain.Negotiable);
            DeviceExtensionSet.Selection chosen = DeviceExtensionSet.Choose(new HashSet<string>(adapter.Candidate.DeviceExtensions.ToArray(), StringComparer.Ordinal), Instance.EnabledExtensions);
            string? maintenance1 = chosen.SwapchainMaintenance1;
            chosen = chosen with { Extensions = Array.FindAll(chosen.Extensions, name => name != maintenance1), SwapchainMaintenance1 = null };
            created = VulkanDeviceCore.Create(Instance, adapter.Device, adapter.Family, features, chosen, gate);
        }
        else
        {
            created = GpuDevices.Create(Instance, adapter, gate, null);
        }

        Assert.True(created.IsSuccess, created.ToString());
        return created.Value;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            // Sound: the surface was made from this live instance, whose table was loaded with VK_KHR_surface enabled
            // (the window's extensions); no allocator was used.
            Instance.Brutal.PInvoke.vkDestroySurfaceKHR.Invoke(Instance.Handle, Surface, null);
            Instance.Dispose();
        }
        finally
        {
            _window.Dispose();
        }
    }
}
