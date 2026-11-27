using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Brutal.VulkanApi;
using BrutalEngine.Core;
using BrutalEngine.Core.Logging;
using BrutalEngine.Graphics.Backend;
using BrutalEngine.Graphics.Vulkan.Instances;

namespace BrutalEngine.Graphics.Vulkan.Adapters;

/// <summary>
/// <c>EnumerateAdapters</c> (docs/design/graphics-vulkan.md section 3): creates an instance and, with a compatible
/// surface, a temporary surface; reads every physical device (<see cref="CandidateReader"/>) and judges it
/// (<see cref="AdapterRules"/>); then destroys the surface and the instance, on every path.
/// </summary>
/// <remarks>
/// <para>
/// The adapters keep BRUTAL's enumeration order, with <see cref="GraphicsAdapterInfo.Index"/> equal to the position. Each
/// one is logged at <see cref="LogLevel.Information"/> as it is read. A failure of any call ends the enumeration with
/// that call's error; nothing created outlives the call.
/// </para>
/// <para>
/// Creation-time only, on the thread that will own the device (the main thread when there is a surface, which
/// <see cref="WindowSurfaceSource.CreateVulkanSurface"/> requires). Allocates.
/// </para>
/// </remarks>
internal static class AdapterEnumeration
{
    /// <summary>Lists the adapters.</summary>
    /// <param name="request">What to enumerate with.</param>
    /// <param name="gate">The gate every result-returning call passes through.</param>
    /// <returns>
    /// The adapters; or the failure of instance creation (<see cref="InstanceFactory.Create(InstanceFactory.Options, VulkanCallGate)"/>),
    /// of the surface source's <c>CreateVulkanSurface</c> (unchanged), or of reading a device.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> or <paramref name="gate"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An allow-list entry has no reason.</exception>
    public static unsafe Result<IReadOnlyList<GraphicsAdapterInfo>> Enumerate(Request request, VulkanCallGate gate)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(gate);
        Result<VulkanInstance> created = InstanceFactory.Create(request.Instance, gate);
        if (!created.TryGetValue(out VulkanInstance? instance))
        {
            return created.Error;
        }

        using (instance)
        {
            VkSurfaceKHR surface = default;
            if (request.CreateSurface is { } createSurface)
            {
                Result<nint> made = createSurface(instance.Handle.VkHandle);
                if (!made.TryGetValue(out nint handle))
                {
                    return made.Error;
                }

                surface = new VkSurfaceKHR(handle);
            }

            try
            {
                return Read(instance.Brutal.PhysicalDevices, surface, request, gate);
            }
            finally
            {
                if (!surface.IsNull())
                {
                    // Sound: the surface was created from this live instance, whose table was loaded with VK_KHR_surface
                    // enabled (the surface's own extensions); no allocator was used.
                    instance.Brutal.PInvoke.vkDestroySurfaceKHR.Invoke(instance.Handle, surface, null);
                    request.SurfaceDestroys?.OnDestroyed();
                }
            }
        }
    }

    private static Result<IReadOnlyList<GraphicsAdapterInfo>> Read(PhysicalDevice[] devices, VkSurfaceKHR surface, Request request, VulkanCallGate gate)
    {
        AdapterRules.RulesRequest rules = new(request.RequiredFeatures, HasCompatibleSurface: !surface.IsNull());
        var adapters = new GraphicsAdapterInfo[devices.Length];
        for (int i = 0; i < devices.Length; i++)
        {
            Result<AdapterCandidate> read = CandidateReader.Read(devices[i], surface, gate);
            if (!read.TryGetValue(out AdapterCandidate candidate))
            {
                return read.Error;
            }

            List<string> reasons = [];
            AdapterRules.AppendRejections(candidate, rules, reasons);
            adapters[i] = new GraphicsAdapterInfo
            {
                Index = i,
                Name = candidate.Name,
                Type = candidate.Type,
                Driver = DriverNames.Get(candidate.Driver),
                ApiVersion = string.Create(CultureInfo.InvariantCulture, $"Vulkan {candidate.ApiVersionMajor}.{candidate.ApiVersionMinor}.{candidate.ApiVersionPatch}"),
                RejectionReasons = reasons,
            };
            string description = adapters[i].ToString();
            request.Log.Information($"Vulkan adapter: {description}");
        }

        return new Result<IReadOnlyList<GraphicsAdapterInfo>>(adapters);
    }

    /// <summary>What to enumerate with: the instance's part of the device request, the surface and the rules' part.</summary>
    internal sealed record Request
    {
        /// <summary>Gets the instance request; its <see cref="InstanceFactory.Options.SurfaceExtensions"/> are the surface's.</summary>
        public required InstanceFactory.Options Instance { get; init; }

        /// <summary>
        /// Gets the call that creates the temporary surface on the instance's raw handle, or <see langword="null"/> without
        /// a compatible surface.
        /// </summary>
        public Func<nint, Result<nint>>? CreateSurface { get; init; }

        /// <summary>Gets the features every adapter must have.</summary>
        public GraphicsFeatures RequiredFeatures { get; init; }

        /// <summary>Gets where each adapter is logged; disabled by default.</summary>
        public LogCategory Log { get; init; }

        /// <summary>Gets a test's counter of destroyed surfaces, or <see langword="null"/>.</summary>
        public InstanceFactory.DestroyTracker? SurfaceDestroys { get; init; }

        /// <summary>Maps a device request onto an enumeration.</summary>
        /// <param name="request">The device request.</param>
        /// <param name="allowList">The backend's validation allow-list.</param>
        /// <returns>
        /// The enumeration request: the application name, the validation flag with the logger's <c>Graphics.Validation</c>
        /// category, and with a compatible surface its instance extensions and its surface creation.
        /// </returns>
        /// <exception cref="ObjectDisposedException">The request's logger, or the surface's window, is disposed.</exception>
        public static Request From(BackendDeviceRequest request, ReadOnlyMemory<ValidationAllowance> allowList)
        {
            WindowSurfaceSource? surface = request.CompatibleSurface;
            string[] extensions = new string[surface?.RequiredInstanceExtensionCount ?? 0];
            for (int i = 0; i < extensions.Length; i++)
            {
                extensions[i] = Encoding.UTF8.GetString(surface!.GetRequiredInstanceExtension(i));
            }

            return new Request
            {
                Instance = new InstanceFactory.Options
                {
                    ApplicationName = request.ApplicationName,
                    Validation = request.Validation,
                    SurfaceExtensions = extensions,
                    ValidationLog = request.Logger.CreateCategory("Graphics.Validation"),
                    ValidationAllowList = allowList,
                },
                CreateSurface = surface is null ? null : surface.CreateVulkanSurface,
                RequiredFeatures = request.RequiredFeatures,
                Log = request.Logger.CreateCategory(VulkanBackend.LogCategoryName),
            };
        }
    }
}
