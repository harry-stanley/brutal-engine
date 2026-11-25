using System;
using Brutal.VulkanApi;
using CsCheck;
using Xunit;

namespace HelloWindow.Tests;

public sealed class SwapchainPlannerTests
{
    // 0xFFFFFFFF: the surface lets the swapchain choose its size (headless and Wayland surfaces).
    private const int Undefined = -1;

    private static VkSurfaceCapabilitiesKHR Capabilities(int currentWidth, int currentHeight) => new()
    {
        MinImageCount = 2,
        MaxImageCount = 8,
        CurrentExtent = new VkExtent2D(currentWidth, currentHeight),
        MinImageExtent = new VkExtent2D(16, 16),
        MaxImageExtent = new VkExtent2D(4096, 2048),
    };

    [Fact]
    public void ChooseExtent_DefinedCurrentExtent_UsesIt()
    {
        SwapchainExtent extent = SwapchainPlanner.ChooseExtent(Capabilities(1024, 768), 800, 600);

        Assert.Equal(new SwapchainExtent(1024, 768), extent);
        Assert.False(extent.IsMinimised);
    }

    [Theory]
    [InlineData(800, 600, 800, 600)]
    [InlineData(8, 4, 16, 16)]
    [InlineData(9000, 9000, 4096, 2048)]
    [InlineData(5000, 10, 4096, 16)]
    public void ChooseExtent_UndefinedCurrentExtent_ClampsWindowPixels(int windowWidth, int windowHeight, int width, int height)
    {
        SwapchainExtent extent = SwapchainPlanner.ChooseExtent(Capabilities(Undefined, Undefined), windowWidth, windowHeight);

        Assert.Equal(new SwapchainExtent(width, height), extent);
    }

    [Theory]
    [InlineData(Undefined, Undefined, 0, 0)]
    [InlineData(Undefined, Undefined, 0, 600)]
    [InlineData(Undefined, Undefined, 800, 0)]
    [InlineData(0, 0, 800, 600)]
    [InlineData(0, 0, 0, 0)]
    public void ChooseExtent_ZeroWindowSize_ReportsMinimised(int currentWidth, int currentHeight, int windowWidth, int windowHeight)
    {
        // A minimised window has a zero pixel size, or (Windows) a zero currentExtent. Clamping to minImageExtent
        // must not turn either into a renderable size.
        SwapchainExtent extent = SwapchainPlanner.ChooseExtent(Capabilities(currentWidth, currentHeight), windowWidth, windowHeight);

        Assert.True(extent.IsMinimised);
    }

    [Fact]
    public void ChooseImageCount_RespectsMinimumPlusOneAndMaximum()
    {
        // max 0 means "no maximum"; otherwise the spec guarantees max >= min >= 1.
        Gen.Select(Gen.Int[1, 16], Gen.Int[0, 32]).Sample((min, maxOrZero) =>
        {
            int max = maxOrZero == 0 ? 0 : Math.Max(min, maxOrZero);
            int count = SwapchainPlanner.ChooseImageCount(min, max);

            Assert.True(count >= min, $"min {min}, max {max}: chose {count}");
            Assert.True(max == 0 || count <= max, $"min {min}, max {max}: chose {count}");
            Assert.Equal(max == 0 ? min + 1 : Math.Min(min + 1, max), count);
        });
    }

    [Fact]
    public void ChooseSurfaceFormat_PrefersBgra8Srgb_ElseFirst()
    {
        VkSurfaceFormatKHR unorm = new() { Format = VkFormat.B8G8R8A8UNorm, ColorSpace = VkColorSpaceKHR.SRGBNonlinearKHR };
        VkSurfaceFormatKHR srgb = new() { Format = VkFormat.B8G8R8A8SRGB, ColorSpace = VkColorSpaceKHR.SRGBNonlinearKHR };
        VkSurfaceFormatKHR srgbWrongSpace = new() { Format = VkFormat.B8G8R8A8SRGB, ColorSpace = VkColorSpaceKHR.ExtendedSRGBLinearEXT };
        VkSurfaceFormatKHR rgba = new() { Format = VkFormat.R8G8B8A8SRGB, ColorSpace = VkColorSpaceKHR.SRGBNonlinearKHR };

        Assert.Equal(srgb, SwapchainPlanner.ChooseSurfaceFormat([unorm, srgbWrongSpace, srgb, rgba]));
        Assert.Equal(srgbWrongSpace, SwapchainPlanner.ChooseSurfaceFormat([srgbWrongSpace, unorm]));
        Assert.Equal(rgba, SwapchainPlanner.ChooseSurfaceFormat([rgba]));
    }

    [Fact]
    public void ChooseSurfaceFormat_NoFormats_Throws()
    {
        Assert.Throws<ArgumentException>(() => SwapchainPlanner.ChooseSurfaceFormat([]));
    }

    [Theory]
    [InlineData(VkCompositeAlphaFlagsKHR.OpaqueBitKHR | VkCompositeAlphaFlagsKHR.InheritBitKHR, VkCompositeAlphaFlagsKHR.OpaqueBitKHR)]
    [InlineData(VkCompositeAlphaFlagsKHR.PostMultipliedBitKHR | VkCompositeAlphaFlagsKHR.InheritBitKHR, VkCompositeAlphaFlagsKHR.PostMultipliedBitKHR)]
    [InlineData(VkCompositeAlphaFlagsKHR.InheritBitKHR, VkCompositeAlphaFlagsKHR.InheritBitKHR)]
    public void ChooseCompositeAlpha_PrefersOpaque_ElseLowestSupported(VkCompositeAlphaFlagsKHR supported, VkCompositeAlphaFlagsKHR expected)
    {
        Assert.Equal(expected, SwapchainPlanner.ChooseCompositeAlpha(supported));
    }

    [Fact]
    public void ChooseCompositeAlpha_NoneSupported_Throws()
    {
        Assert.Throws<ArgumentException>(() => SwapchainPlanner.ChooseCompositeAlpha(VkCompositeAlphaFlagsKHR.None));
    }

    [Fact]
    public void ChooseImageUsage_TransferDstSupported_RequestsIt()
    {
        VkImageUsageFlags usage = SwapchainPlanner.ChooseImageUsage(
            VkImageUsageFlags.ColorAttachmentBit | VkImageUsageFlags.TransferDstBit | VkImageUsageFlags.TransferSrcBit);

        Assert.Equal(VkImageUsageFlags.ColorAttachmentBit | VkImageUsageFlags.TransferDstBit, usage);
    }

    [Fact]
    public void ChooseImageUsage_TransferDstUnsupported_RequestsColourAttachmentOnly()
    {
        // The splash capability check then reports the missing TRANSFER_DST as a setup failure.
        Assert.Equal(VkImageUsageFlags.ColorAttachmentBit, SwapchainPlanner.ChooseImageUsage(VkImageUsageFlags.ColorAttachmentBit));
    }

    [Fact]
    public void ChooseImageUsage_NoColourAttachment_Throws()
    {
        Assert.Throws<SampleFailureException>(() => SwapchainPlanner.ChooseImageUsage(VkImageUsageFlags.TransferDstBit));
    }
}
