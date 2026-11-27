using System;
using System.Collections.Generic;
using Brutal.VulkanApi;
using BrutalEngine.Graphics.Vulkan.Swapchains;

namespace BrutalEngine.Graphics.Vulkan.Tests.Swapchains;

/// <summary>
/// A scripted surface on one adapter: answers the surface queries as a driver would, counts every native call, can grow
/// a list between a count query and its fill (so the fill reports <c>VK_INCOMPLETE</c>, as the specification says for a
/// list larger than the caller's array), and can write fewer entries than it counted.
/// </summary>
internal sealed class FakeSurface : SurfaceQueries.ISource
{
    // A bound of its own, far above the backend's retry limit, so a retry loop that never ends fails the test instead of
    // hanging the test host.
    private const int RunawayFills = 64;

    private List<VkSurfaceFormatKHR>? _formats;
    private List<VkPresentModeKHR>? _modes;

    public VkSurfaceCapabilitiesKHR Capabilities { get; init; }

    public VkResult CapabilitiesResult { get; init; } = VkResult.Success;

    /// <summary>Gets the formats the surface starts with.</summary>
    public VkSurfaceFormatKHR[] Formats { get; init; } = [];

    /// <summary>Gets the present modes the surface starts with.</summary>
    public VkPresentModeKHR[] Modes { get; init; } = [];

    /// <summary>How many format count queries are followed by one more format before the fill.</summary>
    public int FormatGrowths { get; set; }

    /// <summary>How many present-mode count queries are followed by one more mode before the fill.</summary>
    public int ModeGrowths { get; set; }

    /// <summary>How many entries a format fill writes, when fewer than it counted; <see langword="null"/> for all.</summary>
    public int? FormatsWritten { get; init; }

    /// <summary>How many entries a present-mode fill writes, when fewer than it counted; <see langword="null"/> for all.</summary>
    public int? ModesWritten { get; init; }

    /// <summary>Gets the formats as they are now, after every growth.</summary>
    public VkSurfaceFormatKHR[] CurrentFormats => [.. FormatList];

    /// <summary>Gets the present modes as they are now, after every growth.</summary>
    public VkPresentModeKHR[] CurrentModes => [.. ModeList];

    public int CapabilityCalls { get; private set; }

    public int FormatCountCalls { get; private set; }

    public int FormatFillCalls { get; private set; }

    public int ModeCountCalls { get; private set; }

    public int ModeFillCalls { get; private set; }

    /// <summary>Every native call, in order: capabilities, format count and fill, present-mode count and fill.</summary>
    public (int Capabilities, int FormatCounts, int FormatFills, int ModeCounts, int ModeFills) CallCounts =>
        (CapabilityCalls, FormatCountCalls, FormatFillCalls, ModeCountCalls, ModeFillCalls);

    private List<VkSurfaceFormatKHR> FormatList => _formats ??= [.. Formats];

    private List<VkPresentModeKHR> ModeList => _modes ??= [.. Modes];

    public VkResult GetCapabilities(out VkSurfaceCapabilitiesKHR capabilities)
    {
        CapabilityCalls++;
        capabilities = CapabilitiesResult == VkResult.Success ? Capabilities : default;
        return CapabilitiesResult;
    }

    public VkResult GetFormats(ref int count, Span<VkSurfaceFormatKHR> formats)
    {
        List<VkSurfaceFormatKHR> list = FormatList;
        if (formats.IsEmpty)
        {
            FormatCountCalls++;
            count = list.Count;
            if (FormatGrowths > 0)
            {
                // A value the starting lists never use, distinct per growth, so a lost entry shows.
                FormatGrowths--;
                list.Add(new VkSurfaceFormatKHR { Format = (VkFormat)(1000 + list.Count), ColorSpace = VkColorSpaceKHR.SRGBNonlinearKHR });
            }

            return VkResult.Success;
        }

        FormatFillCalls++;
        return Fill(list, FormatsWritten, ref count, formats, FormatFillCalls);
    }

    public VkResult GetPresentModes(ref int count, Span<VkPresentModeKHR> modes)
    {
        List<VkPresentModeKHR> list = ModeList;
        if (modes.IsEmpty)
        {
            ModeCountCalls++;
            count = list.Count;
            if (ModeGrowths > 0)
            {
                ModeGrowths--;
                list.Add((VkPresentModeKHR)(1000 + list.Count));
            }

            return VkResult.Success;
        }

        ModeFillCalls++;
        return Fill(list, ModesWritten, ref count, modes, ModeFillCalls);
    }

    // Writes up to the capacity (fewer when scripted) and reports the written count; VK_INCOMPLETE when the list holds
    // more than the capacity, as the specification says.
    private static VkResult Fill<T>(List<T> all, int? written, ref int count, Span<T> target, int fills)
    {
        if (fills > RunawayFills)
        {
            throw new InvalidOperationException($"{fills} fills: the retry loop isn't bounded.");
        }

        int capacity = Math.Min(count, target.Length);
        int n = Math.Min(Math.Min(capacity, all.Count), written ?? all.Count);
        for (int i = 0; i < n; i++)
        {
            target[i] = all[i];
        }

        count = n;
        return all.Count > capacity ? VkResult.Incomplete : VkResult.Success;
    }
}
