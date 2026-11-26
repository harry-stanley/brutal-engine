using System;
using System.Collections.Generic;
using System.Text;
using Brutal.VulkanApi;
using BrutalEngine.Core;

namespace BrutalEngine.Graphics.Vulkan.Instances;

/// <summary>
/// Chooses the instance's extensions, layers and flags (docs/design/graphics-vulkan.md section 1, the extension table)
/// from what the loader and the validation layer offer, and enumerates those offers through a <see cref="GlobalCommands.ISource"/>.
/// </summary>
/// <remarks>
/// <see cref="Plan"/> is pure. The enumeration helpers make every call through the device's <see cref="VulkanCallGate"/>,
/// with the two-call pattern and a bounded retry on <c>VK_INCOMPLETE</c>, because BRUTAL's <c>_Count</c> helpers drop
/// their result (upstream issue draft #18). Creation-time only: they allocate the arrays they return. Not thread-safe
/// beyond what the gate allows: the device's owner thread calls them.
/// </remarks>
internal static class InstanceExtensions
{
    /// <summary>The Khronos validation layer's name.</summary>
    internal const string ValidationLayer = "VK_LAYER_KHRONOS_validation";

    /// <summary>How many times an enumeration is tried while its list keeps growing between the two calls.</summary>
    internal const int MaxEnumerationAttempts = 8;

    /// <summary>Chooses the instance's extensions, layers and flags. Pure.</summary>
    /// <param name="available">The extensions the loader, the drivers and the implicit layers offer (no layer name).</param>
    /// <param name="layerExtensions">The validation layer's own extensions; empty without validation.</param>
    /// <param name="options">The instance request.</param>
    /// <returns>
    /// The selection, in this order: the surface's required extensions, <c>VK_KHR_portability_enumeration</c> with the
    /// <c>ENUMERATE_PORTABILITY</c> flag when offered, <c>VK_KHR_get_surface_capabilities2</c> and one surface
    /// maintenance1 extension (KHR preferred) when offered and <c>VK_KHR_surface</c> is enabled, then
    /// <c>VK_EXT_debug_utils</c> and, with validation, <c>VK_EXT_validation_features</c>. Or a failure:
    /// <see cref="ErrorKind.Unsupported"/> when a surface extension isn't offered; <see cref="ErrorKind.Unavailable"/>
    /// with validation when <c>VK_EXT_debug_utils</c> isn't offered, or when the layer doesn't offer
    /// <c>VK_EXT_validation_features</c> ("validation layer not installed").
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    public static Result<Selection> Plan(ReadOnlySpan<string> available, ReadOnlySpan<string> layerExtensions, InstanceFactory.Options options)
    {
        ArgumentNullException.ThrowIfNull(options);
        List<string> extensions = [];
        foreach (string required in options.SurfaceExtensions.Span)
        {
            if (!available.Contains(required))
            {
                return new EngineError(ErrorKind.Unsupported, "instance extension not offered: " + required);
            }

            AddOnce(extensions, required);
        }

        VkInstanceCreateFlags flags = VkInstanceCreateFlags.None;
        if (available.Contains(VK.KHR_PORTABILITY_ENUMERATION_EXTENSION_NAME))
        {
            // MoltenVK is listed only with portability enumeration (unverified on macOS).
            extensions.Add(VK.KHR_PORTABILITY_ENUMERATION_EXTENSION_NAME);
            flags |= VkInstanceCreateFlags.EnumeratePortabilityBitKHR;
        }

        // Both depend on VK_KHR_surface (the Vulkan registry), which only a compatible surface enables.
        if (extensions.Contains(VK.KHR_SURFACE_EXTENSION_NAME) && available.Contains(VK.KHR_GET_SURFACE_CAPABILITIES_2_EXTENSION_NAME))
        {
            extensions.Add(VK.KHR_GET_SURFACE_CAPABILITIES_2_EXTENSION_NAME);
            if (available.Contains(VK.KHR_SURFACE_MAINTENANCE_1_EXTENSION_NAME))
            {
                extensions.Add(VK.KHR_SURFACE_MAINTENANCE_1_EXTENSION_NAME);
            }
            else if (available.Contains(VK.EXT_SURFACE_MAINTENANCE_1_EXTENSION_NAME))
            {
                extensions.Add(VK.EXT_SURFACE_MAINTENANCE_1_EXTENSION_NAME);
            }
        }

        bool debugUtils = available.Contains(VK.EXT_DEBUG_UTILS_EXTENSION_NAME);
        if (!options.Validation)
        {
            if (debugUtils)
            {
                extensions.Add(VK.EXT_DEBUG_UTILS_EXTENSION_NAME);
            }

            return new Selection([.. extensions], [], flags, Validation: false);
        }

        // The layer offers VK_EXT_debug_utils itself; the messenger needs it either way.
        if (!debugUtils && !layerExtensions.Contains(VK.EXT_DEBUG_UTILS_EXTENSION_NAME))
        {
            return new EngineError(ErrorKind.Unavailable, "VK_EXT_debug_utils not offered: validation needs it for the messenger");
        }

        if (!layerExtensions.Contains(VK.EXT_VALIDATION_FEATURES_EXTENSION_NAME))
        {
            return new EngineError(ErrorKind.Unavailable, "validation layer not installed: " + ValidationLayer + " doesn't offer VK_EXT_validation_features");
        }

        extensions.Add(VK.EXT_DEBUG_UTILS_EXTENSION_NAME);
        extensions.Add(VK.EXT_VALIDATION_FEATURES_EXTENSION_NAME);
        return new Selection([.. extensions], [ValidationLayer], flags, Validation: true);
    }

    /// <summary>Enumerates what is offered and plans the instance (<see cref="Plan"/>).</summary>
    /// <typeparam name="TSource">The global commands; the real ones are a struct, so nothing is boxed.</typeparam>
    /// <param name="source">The enumeration.</param>
    /// <param name="options">The instance request.</param>
    /// <param name="gate">The gate every enumeration call passes through.</param>
    /// <returns>
    /// <see cref="Plan"/>'s result; an enumeration's failure; or, with validation, <see cref="ErrorKind.Unavailable"/>
    /// ("validation layer not installed") when the layer isn't listed, before its own extensions are asked for.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> or <paramref name="gate"/> is <see langword="null"/>.</exception>
    public static Result<Selection> Select<TSource>(TSource source, InstanceFactory.Options options, VulkanCallGate gate)
        where TSource : GlobalCommands.ISource
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(gate);
        Result<string[]> available = EnumerateExtensions(source, null, gate);
        if (!available.TryGetValue(out string[]? extensions))
        {
            return available.Error;
        }

        string[] layerExtensions = [];
        if (options.Validation)
        {
            Result<string[]> layers = EnumerateLayers(source, gate);
            if (!layers.TryGetValue(out string[]? names))
            {
                return layers.Error;
            }

            if (!names.AsSpan().Contains(ValidationLayer))
            {
                return new EngineError(ErrorKind.Unavailable, "validation layer not installed: " + ValidationLayer + " is missing");
            }

            Result<string[]> fromLayer = EnumerateExtensions(source, ValidationLayer, gate);
            if (!fromLayer.TryGetValue(out string[]? ofLayer))
            {
                return fromLayer.Error;
            }

            layerExtensions = ofLayer;
        }

        return Plan(extensions, layerExtensions, options);
    }

    /// <summary>Lists the instance extensions, with the two-call pattern (<see cref="VulkanCall.EnumerateInstanceExtensionProperties"/>).</summary>
    /// <typeparam name="TSource">The enumeration.</typeparam>
    /// <param name="source">The enumeration.</param>
    /// <param name="layerName">A layer whose own extensions to list, or <see langword="null"/> for the loader's.</param>
    /// <param name="gate">The gate both calls pass through.</param>
    /// <returns>The names; or the gate's failure, including <c>VK_INCOMPLETE</c> after <see cref="MaxEnumerationAttempts"/>.</returns>
    public static unsafe Result<string[]> EnumerateExtensions<TSource>(TSource source, string? layerName, VulkanCallGate gate)
        where TSource : GlobalCommands.ISource
    {
        const VulkanCall call = VulkanCall.EnumerateInstanceExtensionProperties;
        VkResult result = VkResult.Incomplete;
        for (int attempt = 0; attempt < MaxEnumerationAttempts && result == VkResult.Incomplete; attempt++)
        {
            int count = 0;
            result = gate.TryTakeFault(call, out VkResult injected) ? injected : source.EnumerateExtensions(layerName, ref count, []);
            if (result != VkResult.Success || count == 0)
            {
                break;
            }

            var properties = new VkExtensionProperties[count];
            result = gate.TryTakeFault(call, out injected) ? injected : source.EnumerateExtensions(layerName, ref count, properties);
            if (result == VkResult.Success)
            {
                string[] names = new string[count];

                // Sound: the array is pinned while its entries' name buffers are read.
                fixed (VkExtensionProperties* entries = properties)
                {
                    for (int i = 0; i < count; i++)
                    {
                        names[i] = NameOf(entries[i].ExtensionName);
                    }
                }

                return names;
            }
        }

        Result checkedResult = gate.Check(result, call);
        return checkedResult.TryGetError(out EngineError error) ? error : Array.Empty<string>();
    }

    /// <summary>Lists the instance layers, with the two-call pattern (<see cref="VulkanCall.EnumerateInstanceLayerProperties"/>).</summary>
    /// <typeparam name="TSource">The enumeration.</typeparam>
    /// <param name="source">The enumeration.</param>
    /// <param name="gate">The gate both calls pass through.</param>
    /// <returns>The names; or the gate's failure, including <c>VK_INCOMPLETE</c> after <see cref="MaxEnumerationAttempts"/>.</returns>
    public static unsafe Result<string[]> EnumerateLayers<TSource>(TSource source, VulkanCallGate gate)
        where TSource : GlobalCommands.ISource
    {
        const VulkanCall call = VulkanCall.EnumerateInstanceLayerProperties;
        VkResult result = VkResult.Incomplete;
        for (int attempt = 0; attempt < MaxEnumerationAttempts && result == VkResult.Incomplete; attempt++)
        {
            int count = 0;
            result = gate.TryTakeFault(call, out VkResult injected) ? injected : source.EnumerateLayers(ref count, []);
            if (result != VkResult.Success || count == 0)
            {
                break;
            }

            var properties = new VkLayerProperties[count];
            result = gate.TryTakeFault(call, out injected) ? injected : source.EnumerateLayers(ref count, properties);
            if (result == VkResult.Success)
            {
                string[] names = new string[count];

                // Sound: the array is pinned while its entries' name buffers are read.
                fixed (VkLayerProperties* entries = properties)
                {
                    for (int i = 0; i < count; i++)
                    {
                        names[i] = NameOf(entries[i].LayerName);
                    }
                }

                return names;
            }
        }

        Result checkedResult = gate.Check(result, call);
        return checkedResult.TryGetError(out EngineError error) ? error : Array.Empty<string>();
    }

    private static void AddOnce(List<string> names, string name)
    {
        if (!names.Contains(name))
        {
            names.Add(name);
        }
    }

    // Sound: the name is a 256-byte (VK_MAX_EXTENSION_NAME_SIZE) buffer in an entry the caller has pinned, and the
    // span covers exactly that buffer.
    private static unsafe string NameOf(byte* name)
    {
        ReadOnlySpan<byte> bytes = new(name, VK.MAX_EXTENSION_NAME_SIZE);
        int end = bytes.IndexOf((byte)0);
        return Encoding.UTF8.GetString(end < 0 ? bytes : bytes[..end]);
    }

    /// <summary>The instance's planned extensions, layers and flags.</summary>
    /// <param name="Extensions">The extension names to enable, without duplicates.</param>
    /// <param name="Layers">The layer names to enable: the validation layer or none.</param>
    /// <param name="Flags">The instance create flags.</param>
    /// <param name="Validation">Whether the validation layer and synchronization validation are enabled.</param>
    internal sealed record Selection(string[] Extensions, string[] Layers, VkInstanceCreateFlags Flags, bool Validation);
}
