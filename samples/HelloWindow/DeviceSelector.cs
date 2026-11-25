using System.Collections.Generic;
using Brutal.VulkanApi;

namespace HelloWindow;

/// <summary>Chooses the physical device: Vulkan 1.3, VK_KHR_swapchain, dynamicRendering, synchronization2 and a
/// graphics+present queue family are required, and discrete GPUs are preferred (docs/03-architecture.md 3.8).</summary>
internal static class DeviceSelector
{
    /// <summary>Selects the best suitable candidate.</summary>
    /// <param name="candidates">The physical devices, in enumeration order.</param>
    /// <param name="requireCpu">Accept only a CPU (software) device.</param>
    /// <param name="requireDriver">Accept only this driver, or null for any.</param>
    /// <returns>The chosen index (ties go to the first enumerated) and a reason for every rejected candidate.</returns>
    public static DeviceSelection Select(IReadOnlyList<DeviceCandidate> candidates, bool requireCpu, VkDriverId? requireDriver)
    {
        int chosen = -1;
        List<string> rejections = [];
        List<string> reasons = [];
        for (int i = 0; i < candidates.Count; i++)
        {
            DeviceCandidate candidate = candidates[i];
            reasons.Clear();
            if (candidate.ApiVersion < VK.API_VERSION_1_3)
            {
                reasons.Add($"Vulkan {FormatVersion(candidate.ApiVersion)} (needs 1.3)");
            }

            AddIf(reasons, !candidate.SupportsSwapchain, "no VK_KHR_swapchain");
            AddIf(reasons, !candidate.SupportsDynamicRendering, "no dynamicRendering");
            AddIf(reasons, !candidate.SupportsSynchronization2, "no synchronization2");
            AddIf(reasons, candidate.GraphicsPresentQueueFamily < 0, "no queue family with graphics and present support");
            if (requireCpu && candidate.Type != VkPhysicalDeviceType.Cpu)
            {
                reasons.Add($"type {candidate.Type} (needs Cpu)");
            }

            if (requireDriver is { } driver && candidate.DriverId != driver)
            {
                reasons.Add($"driver {candidate.DriverId} (needs {driver})");
            }

            if (reasons.Count > 0)
            {
                rejections.Add($"{candidate.Name}: {string.Join(", ", reasons)}");
            }
            else if (chosen < 0 || Preference(candidate.Type) > Preference(candidates[chosen].Type))
            {
                chosen = i;
            }
        }

        return new DeviceSelection(chosen, rejections);
    }

    /// <summary>Formats a VK_MAKE_API_VERSION value as major.minor.patch.</summary>
    /// <param name="version">The packed version.</param>
    /// <returns>For example <c>1.3.280</c>.</returns>
    public static string FormatVersion(int version) => $"{(version >> 22) & 0x7F}.{(version >> 12) & 0x3FF}.{version & 0xFFF}";

    private static void AddIf(List<string> reasons, bool condition, string reason)
    {
        if (condition)
        {
            reasons.Add(reason);
        }
    }

    private static int Preference(VkPhysicalDeviceType type) => type switch
    {
        VkPhysicalDeviceType.DiscreteGpu => 4,
        VkPhysicalDeviceType.IntegratedGpu => 3,
        VkPhysicalDeviceType.VirtualGpu => 2,
        VkPhysicalDeviceType.Cpu => 1,
        _ => 0,
    };
}
