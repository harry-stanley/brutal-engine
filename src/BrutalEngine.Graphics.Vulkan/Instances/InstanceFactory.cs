using System;
using System.Collections.Generic;
using System.Text;
using Brutal.VulkanApi;
using BrutalEngine.Core;

namespace BrutalEngine.Graphics.Vulkan.Instances;

/// <summary>
/// Creates the device's <see cref="VulkanInstance"/> (docs/design/graphics-vulkan.md section 1): enumerates what is
/// offered, plans the extensions, layers and flags (<see cref="InstanceExtensions"/>), then creates the instance through
/// BRUTAL's <c>Vulkan.CreateInstance</c>, the only way to get BRUTAL's instance function table.
/// </summary>
/// <remarks>
/// <para>
/// Every result-returning call goes through the device's <see cref="VulkanCallGate"/> with its own
/// <see cref="VulkanCall"/> member. The wrapper throws <see cref="VkResultError"/>, so this is one of the three call
/// sites that catch it and convert it with <see cref="VkCheck.FromException"/> (section 13, rule 2); its gate check
/// runs before the wrapper, so an injected fault creates nothing.
/// </para>
/// <para>
/// A <see cref="VulkanBackend"/> must exist first: its creation bound BRUTAL's <c>Vulkan</c> class to the process's
/// loader. Creation-time only, on the device's owner thread; allocates the selection, BRUTAL's sets and the instance.
/// </para>
/// </remarks>
internal static class InstanceFactory
{
    /// <summary>The wrapper call's name in a converted error's message.</summary>
    private const string CreateInstanceCall = "vkCreateInstance";

    /// <summary>Creates an instance on the process's loader.</summary>
    /// <param name="options">The request.</param>
    /// <param name="gate">The device's gate.</param>
    /// <returns>
    /// The instance, owned by the caller; or the failure of an enumeration, of the plan (<see cref="InstanceExtensions.Plan"/>,
    /// <see cref="InstanceExtensions.Select"/>), or of instance creation, with <see cref="VkCheck.KindOf"/>'s kind and the code.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> or <paramref name="gate"/> is <see langword="null"/>.</exception>
    public static Result<VulkanInstance> Create(Options options, VulkanCallGate gate) =>
        Create(options, gate, default(InstanceExtensions.LoaderEnumeration));

    /// <summary><see cref="Create(Options, VulkanCallGate)"/> over a given enumeration.</summary>
    /// <typeparam name="TSource">The enumeration.</typeparam>
    /// <param name="options">The request.</param>
    /// <param name="gate">The device's gate.</param>
    /// <param name="source">The enumeration.</param>
    /// <returns>As <see cref="Create(Options, VulkanCallGate)"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> or <paramref name="gate"/> is <see langword="null"/>.</exception>
    internal static Result<VulkanInstance> Create<TSource>(Options options, VulkanCallGate gate, TSource source)
        where TSource : InstanceExtensions.IEnumeration
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(gate);
        Result<InstanceExtensions.Selection> selected = InstanceExtensions.Select(source, options, gate);
        return selected.TryGetValue(out InstanceExtensions.Selection? selection)
            ? Create(selection, options.ApplicationName, gate)
            : selected.Error;
    }

    /// <summary>Creates an instance from a selection, as planned or as a test builds it.</summary>
    /// <param name="selection">The extensions, layers and flags.</param>
    /// <param name="applicationName">The application name in the instance's application info.</param>
    /// <param name="gate">The device's gate.</param>
    /// <returns>The instance; or the injected fault, or BRUTAL's <see cref="VkResultError"/> as an error.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    internal static unsafe Result<VulkanInstance> Create(InstanceExtensions.Selection selection, string applicationName, VulkanCallGate gate)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(applicationName);
        ArgumentNullException.ThrowIfNull(gate);
        if (gate.TryTakeFault(VulkanCall.CreateInstance, out VkResult injected))
        {
            return gate.Check(injected, VulkanCall.CreateInstance).Error;
        }

        byte[] name = new byte[Encoding.UTF8.GetByteCount(applicationName) + 1];
        _ = Encoding.UTF8.GetBytes(applicationName, name);
        VkValidationFeatureEnableEXT synchronization = VkValidationFeatureEnableEXT.SynchronizationValidationEXT;
        Instance instance;

        // Sound: the name is pinned and NUL-terminated, and the features struct and its one-entry array are stack
        // locals; all of them outlive Vulkan.CreateInstance, and the loader copies what it keeps. GPU-assisted and
        // best-practices validation stay off: they aren't enabled, and both are off by default.
        fixed (byte* pName = name)
        {
            VkValidationFeaturesEXT features = new() { EnabledValidationFeatureCount = 1, EnabledValidationFeatures = &synchronization };
            try
            {
                instance = global::Brutal.VulkanApi.Vulkan.CreateInstance(new Instance.CreateInfo
                {
                    AppInfo = new VkApplicationInfo { ApplicationName = pName, ApiVersion = VK.API_VERSION_1_3 },
                    Flags = selection.Flags,
                    EnabledExtensions = new HashSet<string>(selection.Extensions, StringComparer.Ordinal),
                    EnabledLayers = new HashSet<string>(selection.Layers, StringComparer.Ordinal),
                    Next = selection.Validation ? &features : null,
                });
            }
            catch (VkResultError error)
            {
                // Also reached when BRUTAL's constructor fails to enumerate the physical devices after creating the
                // instance; that instance then leaks until BRUTAL's finalizer runs (upstream issue draft #20).
                return VkCheck.FromException(error, CreateInstanceCall);
            }
        }

        return new VulkanInstance(instance, selection);
    }

    /// <summary>
    /// The instance's part of a device request: a stand-in for <c>BackendDeviceRequest</c> (docs/design/graphics.md,
    /// "The backend interface"), which doesn't exist yet. The device fills it from the request's fields.
    /// </summary>
    internal sealed record Options
    {
        /// <summary>Gets the application name; empty by default.</summary>
        public string ApplicationName { get; init; } = string.Empty;

        /// <summary>Gets whether the validation layer, synchronization validation and <c>VK_EXT_debug_utils</c> are required.</summary>
        public bool Validation { get; init; }

        /// <summary>Gets the compatible surface's required instance extensions; empty for an offscreen-only device.</summary>
        public ReadOnlyMemory<string> SurfaceExtensions { get; init; }
    }
}
