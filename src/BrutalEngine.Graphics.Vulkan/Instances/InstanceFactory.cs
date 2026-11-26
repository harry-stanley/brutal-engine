using System;
using System.Collections.Generic;
using System.Text;
using Brutal.VulkanApi;
using BrutalEngine.Core;
using BrutalEngine.Core.Logging;

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
/// With validation, a <see cref="ValidationSink"/> is created first. Its messenger description is chained behind the
/// validation features into <c>vkCreateInstance</c>, so creation and destruction are validated and counted; then a
/// persistent messenger is created through the instance's function table. <see cref="VulkanInstance"/> owns both and
/// tears down in reverse: the messenger, the instance, then <see cref="ValidationSink.Release"/>.
/// </para>
/// <para>
/// A <see cref="VulkanBackend"/> must exist first: its creation bound BRUTAL's <c>Vulkan</c> class to the process's
/// loader. Creation-time only, on the device's owner thread; allocates the selection, BRUTAL's sets, the sink and the
/// instance.
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
    /// <see cref="InstanceExtensions.Select"/>), of instance creation or of the messenger, with <see cref="VkCheck.KindOf"/>'s
    /// kind and the code.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> or <paramref name="gate"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An allow-list entry has no reason.</exception>
    public static Result<VulkanInstance> Create(Options options, VulkanCallGate gate) =>
        Create(options, gate, default(InstanceExtensions.LoaderEnumeration));

    /// <summary><see cref="Create(Options, VulkanCallGate)"/> over a given enumeration.</summary>
    /// <typeparam name="TSource">The enumeration.</typeparam>
    /// <param name="options">The request.</param>
    /// <param name="gate">The device's gate.</param>
    /// <param name="source">The enumeration.</param>
    /// <returns>As <see cref="Create(Options, VulkanCallGate)"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> or <paramref name="gate"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An allow-list entry has no reason.</exception>
    internal static Result<VulkanInstance> Create<TSource>(Options options, VulkanCallGate gate, TSource source)
        where TSource : InstanceExtensions.IEnumeration
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(gate);
        Result<InstanceExtensions.Selection> selected = InstanceExtensions.Select(source, options, gate);
        return selected.TryGetValue(out InstanceExtensions.Selection? selection)
            ? Create(selection, options, gate)
            : selected.Error;
    }

    /// <summary>Creates an instance from a selection, with no validation log or allow-list.</summary>
    /// <param name="selection">The extensions, layers and flags.</param>
    /// <param name="applicationName">The application name in the instance's application info.</param>
    /// <param name="gate">The device's gate.</param>
    /// <returns>As <see cref="Create(InstanceExtensions.Selection, Options, VulkanCallGate)"/>.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    internal static Result<VulkanInstance> Create(InstanceExtensions.Selection selection, string applicationName, VulkanCallGate gate)
    {
        ArgumentNullException.ThrowIfNull(applicationName);
        return Create(selection, new Options { ApplicationName = applicationName }, gate);
    }

    /// <summary>Creates an instance from a selection, as planned or as a test builds it.</summary>
    /// <param name="selection">The extensions, layers and flags; with validation, the sink and messenger too.</param>
    /// <param name="options">The application name and, with validation, the sink's log category and allow-list.</param>
    /// <param name="gate">The device's gate.</param>
    /// <returns>
    /// The instance; or the injected fault, BRUTAL's <see cref="VkResultError"/>, or the messenger's failure as an error.
    /// A failure after the sink exists releases it, unless the native instance may still exist
    /// (<see cref="IsCreateInstanceFailure"/>).
    /// </returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An allow-list entry has no reason; thrown before any native call.</exception>
    internal static unsafe Result<VulkanInstance> Create(InstanceExtensions.Selection selection, Options options, VulkanCallGate gate)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(gate);
        if (gate.TryTakeFault(VulkanCall.CreateInstance, out VkResult injected))
        {
            return gate.Check(injected, VulkanCall.CreateInstance).Error;
        }

        byte[] name = new byte[Encoding.UTF8.GetByteCount(options.ApplicationName) + 1];
        _ = Encoding.UTF8.GetBytes(options.ApplicationName, name);
        ValidationSink? sink = selection.Validation
            ? new ValidationSink(options.ValidationLog, options.ValidationAllowList, options.ValidationHandles)
            : null;
        VkDebugUtilsMessengerCreateInfoEXT messengerInfo = sink is null ? default : ValidationCallback.CreateInfo(sink);
        VkValidationFeatureEnableEXT synchronization = VkValidationFeatureEnableEXT.SynchronizationValidationEXT;
        Instance instance;

        // Sound: the name is pinned and NUL-terminated, and the features struct, its one-entry array and the messenger
        // description are stack locals; all of them outlive Vulkan.CreateInstance, and the loader copies what it keeps.
        // The chain is the features, then the messenger description. GPU-assisted and best-practices validation stay
        // off: they aren't enabled, and both are off by default.
        fixed (byte* pName = name)
        {
            VkValidationFeaturesEXT features = new() { Next = &messengerInfo, EnabledValidationFeatureCount = 1, EnabledValidationFeatures = &synchronization };
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
                // instance; that instance then leaks until BRUTAL's finalizer runs (upstream issue draft #20), and the
                // sink stays alive with it.
                if (sink is not null && IsCreateInstanceFailure(error))
                {
                    sink.Release();
                }

                return VkCheck.FromException(error, CreateInstanceCall);
            }
        }

        if (sink is null)
        {
            return new VulkanInstance(instance, selection, default, null);
        }

        // Sound: the description is a stack local that outlives the call, and the handle is written to a local. The table
        // is the instance's own, loaded with VK_EXT_debug_utils enabled (a validated selection always enables it).
        VkDebugUtilsMessengerEXT messenger = default;
        VkResult result = gate.TryTakeFault(VulkanCall.CreateDebugUtilsMessenger, out VkResult fault)
            ? fault
            : instance.PInvoke.vkCreateDebugUtilsMessengerEXT.Invoke(instance.Handle, &messengerInfo, null, &messenger);
        Result created = gate.Check(result, VulkanCall.CreateDebugUtilsMessenger);
        if (created.IsFailure)
        {
            // The instance exists and nothing else holds it: destroy it, then free the handle that its chained messenger
            // may still call back with during vkDestroyInstance.
            instance.Dispose();
            sink.Release();
            return created.Error;
        }

        return new VulkanInstance(instance, selection, messenger, sink);
    }

    /// <summary>
    /// Whether BRUTAL's creation exception is <c>vkCreateInstance</c>'s own failure, so no native instance exists and
    /// the sink can be released. Otherwise BRUTAL's constructor failed after creating the instance (the physical-device
    /// enumeration, <c>Brutal.Vulkan/Manual/Classes/Instance.cs:94-103</c>): the instance leaks until BRUTAL's finalizer
    /// destroys it (<c>:108-129</c>), and the chained messenger can call back during that destroy, so the sink's handle
    /// is deliberately kept, alive as long as the leaked instance.
    /// </summary>
    /// <param name="error">BRUTAL's exception.</param>
    /// <returns>
    /// <see langword="true"/> for the messages BRUTAL's generated <c>vkCreateInstance</c> binding throws
    /// (<c>Brutal.Vulkan/Generated/PInvoke/VulkanApi/VulkanApiEx.gen.cs:30-60</c>).
    /// </returns>
    internal static bool IsCreateInstanceFailure(VkResultError error) =>
        error.Message.StartsWith("Vulkan call to vkCreateInstance ", StringComparison.Ordinal);

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

        /// <summary>Gets where validation messages are logged (the device logger's <c>Graphics.Validation</c> category); disabled by default.</summary>
        public LogCategory ValidationLog { get; init; }

        /// <summary>Gets the tolerated validation messages; empty by default.</summary>
        public ReadOnlyMemory<ValidationAllowance> ValidationAllowList { get; init; }

        /// <summary>Gets a test's counter of the sink's handle, or <see langword="null"/>.</summary>
        public ValidationSink.HandleTracker? ValidationHandles { get; init; }
    }
}
