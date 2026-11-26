using System;
using System.Collections.Generic;
using Brutal.VulkanApi;
using BrutalEngine.Core;

namespace BrutalEngine.Graphics.Vulkan.Instances;

/// <summary>
/// Chooses the instance's extensions, layers and flags (docs/design/graphics-vulkan.md section 1, the extension table)
/// from what the loader and the validation layer offer.
/// </summary>
/// <remarks>
/// <see cref="Plan"/> is pure and thread-safe. It allocates the selection it returns: creation-time only.
/// </remarks>
internal static class InstanceExtensions
{
    /// <summary>The Khronos validation layer's name.</summary>
    internal const string ValidationLayer = "VK_LAYER_KHRONOS_validation";

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

    private static void AddOnce(List<string> names, string name)
    {
        if (!names.Contains(name))
        {
            names.Add(name);
        }
    }

    /// <summary>The instance's planned extensions, layers and flags.</summary>
    /// <param name="Extensions">The extension names to enable, without duplicates.</param>
    /// <param name="Layers">The layer names to enable: the validation layer or none.</param>
    /// <param name="Flags">The instance create flags.</param>
    /// <param name="Validation">Whether the validation layer and synchronization validation are enabled.</param>
    internal sealed record Selection(string[] Extensions, string[] Layers, VkInstanceCreateFlags Flags, bool Validation);
}
