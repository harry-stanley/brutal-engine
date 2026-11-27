using System;
using Brutal.VulkanApi;
using BrutalEngine.Graphics.Vulkan.Swapchains;

namespace BrutalEngine.Graphics.Vulkan.Tests.Swapchains;

/// <summary>
/// A scripted surface on one adapter: answers the surface queries as a driver would, counts every native call, and can
/// keep reporting <c>VK_INCOMPLETE</c> or write fewer entries than it counted.
/// </summary>
internal sealed class FakeSurface : SurfaceQueries.ISource
{
    // A bound of its own, far above the backend's retry limit, so a retry loop that never ends fails the test instead of
    // hanging the test host.
    private const int RunawayFills = 64;

    public VkSurfaceCapabilitiesKHR Capabilities { get; init; }

    public VkResult CapabilitiesResult { get; init; } = VkResult.Success;

    public VkSurfaceFormatKHR[] Formats { get; init; } = [];

    public VkPresentModeKHR[] Modes { get; init; } = [];

    /// <summary>How many format fills report <c>VK_INCOMPLETE</c> before one succeeds.</summary>
    public int FormatIncompleteFills { get; set; }

    /// <summary>How many present-mode fills report <c>VK_INCOMPLETE</c> before one succeeds.</summary>
    public int ModeIncompleteFills { get; set; }

    /// <summary>How many entries a format fill writes, when fewer than it counted; <see langword="null"/> for all.</summary>
    public int? FormatsWritten { get; init; }

    /// <summary>How many entries a present-mode fill writes, when fewer than it counted; <see langword="null"/> for all.</summary>
    public int? ModesWritten { get; init; }

    public int CapabilityCalls { get; private set; }

    public int FormatCountCalls { get; private set; }

    public int FormatFillCalls { get; private set; }

    public int ModeCountCalls { get; private set; }

    public int ModeFillCalls { get; private set; }

    /// <summary>Every native call, in order: capabilities, format count and fill, present-mode count and fill.</summary>
    public (int Capabilities, int FormatCounts, int FormatFills, int ModeCounts, int ModeFills) CallCounts =>
        (CapabilityCalls, FormatCountCalls, FormatFillCalls, ModeCountCalls, ModeFillCalls);

    public VkResult GetCapabilities(out VkSurfaceCapabilitiesKHR capabilities)
    {
        CapabilityCalls++;
        capabilities = CapabilitiesResult == VkResult.Success ? Capabilities : default;
        return CapabilitiesResult;
    }

    public VkResult GetFormats(ref int count, Span<VkSurfaceFormatKHR> formats)
    {
        if (formats.IsEmpty)
        {
            FormatCountCalls++;
            count = Formats.Length;
            return VkResult.Success;
        }

        FormatFillCalls++;
        int incomplete = FormatIncompleteFills;
        FormatIncompleteFills = Math.Max(0, incomplete - 1);
        return Fill(Formats, FormatsWritten, ref count, formats, incomplete > 0, FormatFillCalls);
    }

    public VkResult GetPresentModes(ref int count, Span<VkPresentModeKHR> modes)
    {
        if (modes.IsEmpty)
        {
            ModeCountCalls++;
            count = Modes.Length;
            return VkResult.Success;
        }

        ModeFillCalls++;
        int incomplete = ModeIncompleteFills;
        ModeIncompleteFills = Math.Max(0, incomplete - 1);
        return Fill(Modes, ModesWritten, ref count, modes, incomplete > 0, ModeFillCalls);
    }

    // Writes up to the capacity (fewer when scripted) and reports the written count, as the specification says.
    private static VkResult Fill<T>(T[] all, int? written, ref int count, Span<T> target, bool incomplete, int fills)
    {
        if (fills > RunawayFills)
        {
            throw new InvalidOperationException($"{fills} fills: the retry loop isn't bounded.");
        }

        int n = Math.Min(Math.Min(count, target.Length), written ?? all.Length);
        all.AsSpan(0, n).CopyTo(target);
        count = n;
        return incomplete ? VkResult.Incomplete : VkResult.Success;
    }
}
