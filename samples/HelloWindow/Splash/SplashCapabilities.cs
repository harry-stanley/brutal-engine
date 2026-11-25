using System.Collections.Generic;
using Brutal.VulkanApi;

namespace HelloWindow.Splash;

/// <summary>Chooses how to get the logo onto the swapchain image: a scaling blit if the surface and format allow it,
/// else a 1:1 copy if the logo fits unscaled, else a setup failure. The splash is a licence duty (BRUTAL LICENSE 2.2),
/// so it is never silently skipped.</summary>
internal static class SplashCapabilities
{
    // Blit: the logo is the source and the swapchain image the destination, scaled with a linear filter, and the logo is
    // uploaded with vkCmdCopyBufferToImage (TRANSFER_DST).
    private const VkFormatFeatureFlags BlitFeatures = VkFormatFeatureFlags.BlitSrcBit | VkFormatFeatureFlags.BlitDstBit
        | VkFormatFeatureFlags.SampledImageFilterLinearBit | VkFormatFeatureFlags.TransferDstBit;

    // Copy: the logo is the source and the swapchain image the destination; the upload also writes the logo.
    private const VkFormatFeatureFlags CopyFeatures = VkFormatFeatureFlags.TransferSrcBit | VkFormatFeatureFlags.TransferDstBit;

    /// <summary>Chooses the transfer.</summary>
    /// <param name="support">What the surface and device offer.</param>
    /// <param name="logo">The logo's size in pixels.</param>
    /// <param name="destination">Where the logo goes (<see cref="SplashLayout.Fit"/>).</param>
    /// <returns>The plan, or an unsupported plan whose message names everything missing.</returns>
    public static SplashPlan Choose(in SplashSurfaceSupport support, PixelSize logo, PixelRect destination)
    {
        VkSurfaceFormatKHR format = support.SwapchainFormat;
        if (format.ColorSpace != VkColorSpaceKHR.SRGBNonlinearKHR || !TryGetPixelOrder(format.Format, out LogoPixelOrder order))
        {
            // The logo is uploaded in the swapchain's own format, so a copy moves bytes unchanged and a blit only
            // filters; that is exact only for the 8-bit formats the decoded PNG maps onto.
            return Unsupported(
                $"The splash needs an 8-bit RGBA or BGRA swapchain format with SRGBNonlinearKHR, but the surface's is "
                + $"{format.Format}/{format.ColorSpace}.");
        }

        // A blit and a copy both write the swapchain image, and the upload writes the logo image.
        List<string> missing = [];
        if ((support.SupportedUsage & VkImageUsageFlags.TransferDstBit) == 0)
        {
            missing.Add("the surface doesn't offer TRANSFER_DST swapchain images");
        }

        VkFormatFeatureFlags features = support.OptimalTilingFeatures;
        string? blitMissing = MissingFeatures(features, BlitFeatures);
        if (missing.Count == 0 && blitMissing is null)
        {
            return new SplashPlan(SplashTransfer.Blit, format.Format, order, null);
        }

        string? copyMissing = MissingFeatures(features, CopyFeatures);
        bool fits = destination.Width == logo.Width && destination.Height == logo.Height;
        if (missing.Count == 0 && copyMissing is null && fits)
        {
            return new SplashPlan(SplashTransfer.Copy, format.Format, order, null);
        }

        if (blitMissing is not null)
        {
            missing.Add($"a blit needs {blitMissing}");
        }

        if (copyMissing is not null)
        {
            missing.Add($"a copy needs {copyMissing}");
        }
        else if (!fits)
        {
            missing.Add($"a 1:1 copy needs the {logo} logo to fit, but it must be scaled to {destination.Size}");
        }

        return Unsupported($"The splash can't be drawn on this {format.Format} surface: {string.Join("; ", missing)}.");
    }

    private static SplashPlan Unsupported(string message) => new(SplashTransfer.Unsupported, VkFormat.Undefined, LogoPixelOrder.Rgba, message);

    // The missing flags, named, or null if none are missing.
    private static string? MissingFeatures(VkFormatFeatureFlags features, VkFormatFeatureFlags required)
    {
        VkFormatFeatureFlags missing = required & ~features;
        return missing == 0 ? null : missing.ToString();
    }

    private static bool TryGetPixelOrder(VkFormat format, out LogoPixelOrder order)
    {
        switch (format)
        {
            case VkFormat.R8G8B8A8SRGB or VkFormat.R8G8B8A8UNorm:
                order = LogoPixelOrder.Rgba;
                return true;
            case VkFormat.B8G8R8A8SRGB or VkFormat.B8G8R8A8UNorm:
                order = LogoPixelOrder.Bgra;
                return true;
            default:
                order = default;
                return false;
        }
    }
}
