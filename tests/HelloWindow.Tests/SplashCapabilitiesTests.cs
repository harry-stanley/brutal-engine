using System;
using Brutal.VulkanApi;
using HelloWindow.Splash;
using Xunit;

namespace HelloWindow.Tests;

public sealed class SplashCapabilitiesTests
{
    private const VkFormatFeatureFlags BlitFeatures = VkFormatFeatureFlags.BlitSrcBit | VkFormatFeatureFlags.BlitDstBit
        | VkFormatFeatureFlags.SampledImageFilterLinearBit | VkFormatFeatureFlags.TransferSrcBit | VkFormatFeatureFlags.TransferDstBit;

    private const VkFormatFeatureFlags CopyFeatures = VkFormatFeatureFlags.TransferSrcBit | VkFormatFeatureFlags.TransferDstBit;

    private const VkImageUsageFlags Usage = VkImageUsageFlags.ColorAttachmentBit | VkImageUsageFlags.TransferDstBit;

    private static PixelSize Logo => new(712, 168);

    // The logo at 1:1, and scaled down to fit a small window.
    private static PixelRect Unscaled => new(284, 276, 712, 168);

    private static PixelRect Scaled => new(20, 108, 360, 84);

    private static SplashSurfaceSupport Support(
        VkFormatFeatureFlags features,
        VkImageUsageFlags usage = Usage,
        VkFormat format = VkFormat.B8G8R8A8SRGB,
        VkColorSpaceKHR colorSpace = VkColorSpaceKHR.SRGBNonlinearKHR) =>
        new(new VkSurfaceFormatKHR { Format = format, ColorSpace = colorSpace }, usage, features);

    [Fact]
    public void Choose_BlitSupported_ChoosesBlitInSwapchainFormat()
    {
        SplashPlan plan = SplashCapabilities.Choose(Support(BlitFeatures), Logo, Scaled);

        Assert.Equal(new SplashPlan(SplashTransfer.Blit, VkFormat.B8G8R8A8SRGB, LogoPixelOrder.Bgra, null), plan);
        Assert.True(plan.IsSupported);
    }

    [Fact]
    public void Choose_BlitSupportedAndLogoFits_StillChoosesBlit()
    {
        SplashPlan plan = SplashCapabilities.Choose(Support(BlitFeatures), Logo, Unscaled);

        Assert.Equal(SplashTransfer.Blit, plan.Transfer);
    }

    [Fact]
    public void Choose_NoBlitSupport_FallsBackToCopyWhenItFits()
    {
        SplashPlan plan = SplashCapabilities.Choose(Support(CopyFeatures), Logo, Unscaled);

        Assert.Equal(new SplashPlan(SplashTransfer.Copy, VkFormat.B8G8R8A8SRGB, LogoPixelOrder.Bgra, null), plan);
    }

    [Theory]
    [InlineData(VkFormatFeatureFlags.BlitSrcBit)]
    [InlineData(VkFormatFeatureFlags.BlitDstBit)]
    [InlineData(VkFormatFeatureFlags.SampledImageFilterLinearBit)]
    public void Choose_OneBlitFeatureMissing_FallsBackToCopy(VkFormatFeatureFlags missing)
    {
        SplashPlan plan = SplashCapabilities.Choose(Support(BlitFeatures & ~missing), Logo, Unscaled);

        Assert.Equal(SplashTransfer.Copy, plan.Transfer);
    }

    [Fact]
    public void Choose_NoBlitSupportAndLogoMustShrink_IsSetupFailure()
    {
        SplashPlan plan = SplashCapabilities.Choose(Support(CopyFeatures), Logo, Scaled);

        Assert.False(plan.IsSupported);
        Assert.Contains("360x84", plan.FailureMessage, StringComparison.Ordinal);
        Assert.Contains("BlitSrcBit", plan.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Choose_NeitherSupported_IsSetupFailure()
    {
        SplashPlan plan = SplashCapabilities.Choose(Support(VkFormatFeatureFlags.None), Logo, Unscaled);

        Assert.Equal(SplashTransfer.Unsupported, plan.Transfer);
        Assert.False(plan.IsSupported);
        Assert.Contains("B8G8R8A8SRGB", plan.FailureMessage, StringComparison.Ordinal);
        Assert.Contains("TransferDstBit", plan.FailureMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(VkFormatFeatureFlags.TransferSrcBit)]
    [InlineData(VkFormatFeatureFlags.TransferDstBit)]
    public void Choose_TransferFeatureMissing_IsSetupFailure(VkFormatFeatureFlags missing)
    {
        // Uploading needs TRANSFER_DST on the logo image; copying needs TRANSFER_SRC too; a blit and a copy both need
        // TRANSFER_DST on the swapchain image. Without blit features the copy is the only option, and it fails.
        SplashPlan plan = SplashCapabilities.Choose(Support(CopyFeatures & ~missing), Logo, Unscaled);

        Assert.False(plan.IsSupported);
    }

    [Fact]
    public void Choose_BlitWithoutTransferDstFeature_IsSetupFailure()
    {
        // The logo is uploaded with vkCmdCopyBufferToImage, which needs TRANSFER_DST even on the blit path.
        SplashPlan plan = SplashCapabilities.Choose(Support(BlitFeatures & ~VkFormatFeatureFlags.TransferDstBit), Logo, Scaled);

        Assert.False(plan.IsSupported);
    }

    [Fact]
    public void Choose_BlitWithoutTransferSrcFeature_IsSetupFailure()
    {
        // The logo image is the blit's source, so it needs TRANSFER_SRC (VUID-vkCmdBlitImage-srcImage-00219). Without
        // it the copy is ruled out too, so there is no way left.
        SplashPlan plan = SplashCapabilities.Choose(Support(BlitFeatures & ~VkFormatFeatureFlags.TransferSrcBit), Logo, Scaled);

        Assert.False(plan.IsSupported);
        Assert.Contains("TransferSrcBit", plan.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Choose_SurfaceWithoutTransferDstUsage_IsSetupFailure()
    {
        SplashPlan plan = SplashCapabilities.Choose(Support(BlitFeatures, usage: VkImageUsageFlags.ColorAttachmentBit), Logo, Unscaled);

        Assert.False(plan.IsSupported);
        Assert.Contains("TRANSFER_DST", plan.FailureMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(VkFormat.A2B10G10R10UNormPack32)]
    [InlineData(VkFormat.R16G16B16A16SFloat)]
    public void Choose_NonEightBitFormat_IsSetupFailure(VkFormat format)
    {
        SplashPlan plan = SplashCapabilities.Choose(Support(BlitFeatures, format: format), Logo, Unscaled);

        Assert.False(plan.IsSupported);
        Assert.Contains(format.ToString(), plan.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Choose_NonSrgbColourSpace_IsSetupFailure()
    {
        SplashPlan plan = SplashCapabilities.Choose(Support(BlitFeatures, colorSpace: VkColorSpaceKHR.ExtendedSRGBLinearEXT), Logo, Unscaled);

        Assert.False(plan.IsSupported);
    }

    [Theory]
    [InlineData(VkFormat.B8G8R8A8SRGB, true)]
    [InlineData(VkFormat.B8G8R8A8UNorm, true)]
    [InlineData(VkFormat.R8G8B8A8SRGB, false)]
    [InlineData(VkFormat.R8G8B8A8UNorm, false)]
    public void Choose_EightBitFormat_UploadsInSwapchainFormatAndOrder(VkFormat format, bool bgra)
    {
        LogoPixelOrder order = bgra ? LogoPixelOrder.Bgra : LogoPixelOrder.Rgba;
        SplashPlan plan = SplashCapabilities.Choose(Support(BlitFeatures, format: format), Logo, Unscaled);

        Assert.Equal(new SplashPlan(SplashTransfer.Blit, format, order, null), plan);
    }

    [Fact]
    public void Plan_Default_IsUnsupported()
    {
        Assert.False(default(SplashPlan).IsSupported);
    }
}
