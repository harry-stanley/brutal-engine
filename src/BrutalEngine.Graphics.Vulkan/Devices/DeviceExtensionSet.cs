using System;
using System.Collections.Generic;
using Brutal.VulkanApi;

namespace BrutalEngine.Graphics.Vulkan.Devices;

/// <summary>
/// Chooses a logical device's extensions (docs/design/graphics-vulkan.md section 3, "Device extensions and the feature
/// chain") from the ones the adapter offers and the instance enabled.
/// </summary>
/// <remarks>
/// <para>The table, in the order the names are listed:</para>
/// <list type="bullet">
/// <item><description>
/// <c>VK_KHR_swapchain</c>: when the instance enabled <c>VK_KHR_surface</c>, which only a compatible surface's
/// extensions do. The adapter rules already rejected adapters without it. The registry makes it depend on
/// <c>VK_KHR_surface</c>, so enabling it on an offscreen-only instance is invalid usage
/// (VUID-vkCreateDevice-ppEnabledExtensionNames-01387, observed under the validation layer 1.4.363).
/// </description></item>
/// <item><description><c>VK_KHR_portability_subset</c>: when offered, because the specification then requires it (unverified on macOS).</description></item>
/// <item><description>
/// <c>VK_KHR_swapchain_maintenance1</c> when offered and the instance enabled <c>VK_KHR_surface_maintenance1</c>, else
/// <c>VK_EXT_swapchain_maintenance1</c> when offered and the instance enabled <c>VK_EXT_surface_maintenance1</c>: each
/// depends on its own surface extension (the same VUID, observed for both mixed pairs).
/// </description></item>
/// <item><description><c>VK_EXT_memory_budget</c>: when offered.</description></item>
/// </list>
/// <para>Pure, stateless and thread-safe. Creation-time only: allocates the selection's array.</para>
/// </remarks>
internal static class DeviceExtensionSet
{
    /// <summary>Chooses the extensions to enable.</summary>
    /// <param name="offered">The extension names the adapter offers, compared as the set compares them (ordinal for the candidate's).</param>
    /// <param name="instanceExtensions">The instance's enabled extensions (<c>VulkanInstance.EnabledExtensions</c>), compared ordinally.</param>
    /// <returns>The selection: the names to enable, in the table's order, and what they enable.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="offered"/> is <see langword="null"/>.</exception>
    public static Selection Choose(IReadOnlySet<string> offered, ReadOnlySpan<string> instanceExtensions)
    {
        ArgumentNullException.ThrowIfNull(offered);
        List<string> names = [];
        if (instanceExtensions.Contains(VK.KHR_SURFACE_EXTENSION_NAME))
        {
            names.Add(VK.KHR_SWAPCHAIN_EXTENSION_NAME);
        }

        bool portability = AddIfOffered(names, offered, VK.KHR_PORTABILITY_SUBSET_EXTENSION_NAME);
        string? maintenance1 = null;
        if (names.Contains(VK.KHR_SWAPCHAIN_EXTENSION_NAME))
        {
            maintenance1 = Pairs(offered, instanceExtensions, VK.KHR_SWAPCHAIN_MAINTENANCE_1_EXTENSION_NAME, VK.KHR_SURFACE_MAINTENANCE_1_EXTENSION_NAME)
                ?? Pairs(offered, instanceExtensions, VK.EXT_SWAPCHAIN_MAINTENANCE_1_EXTENSION_NAME, VK.EXT_SURFACE_MAINTENANCE_1_EXTENSION_NAME);
            if (maintenance1 is not null)
            {
                names.Add(maintenance1);
            }
        }

        bool budget = AddIfOffered(names, offered, VK.EXT_MEMORY_BUDGET_EXTENSION_NAME);
        return new Selection([.. names], portability, maintenance1, budget);
    }

    // The device extension when the adapter offers it and the instance enabled the surface extension it depends on.
    private static string? Pairs(IReadOnlySet<string> offered, ReadOnlySpan<string> instanceExtensions, string device, string surface) =>
        offered.Contains(device) && instanceExtensions.Contains(surface) ? device : null;

    private static bool AddIfOffered(List<string> names, IReadOnlySet<string> offered, string name)
    {
        bool isOffered = offered.Contains(name);
        if (isOffered)
        {
            names.Add(name);
        }

        return isOffered;
    }

    /// <summary>A logical device's chosen extensions.</summary>
    /// <param name="Extensions">The names to enable, without duplicates, in the table's order; empty on an offscreen-only instance without the other extensions.</param>
    /// <param name="PortabilitySubset">Whether <c>VK_KHR_portability_subset</c> is enabled.</param>
    /// <param name="SwapchainMaintenance1">
    /// The enabled swapchain maintenance1 extension, <c>VK_KHR_swapchain_maintenance1</c> or
    /// <c>VK_EXT_swapchain_maintenance1</c>, or <see langword="null"/> for neither; its feature is enabled with it.
    /// </param>
    /// <param name="MemoryBudget">Whether <c>VK_EXT_memory_budget</c> is enabled.</param>
    /// <remarks>Immutable, provided nobody changes the array; safe to read from any thread.</remarks>
    internal readonly record struct Selection(string[] Extensions, bool PortabilitySubset, string? SwapchainMaintenance1, bool MemoryBudget);
}
