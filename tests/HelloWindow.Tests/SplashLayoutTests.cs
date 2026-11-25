using System;
using CsCheck;
using HelloWindow.Splash;
using Xunit;

namespace HelloWindow.Tests;

public sealed class SplashLayoutTests
{
    // The official logo's size (BRUTAL/docs/branding/LOGO_BRUTAL_WHT.png).
    private static PixelSize Logo => new(712, 168);

    [Fact]
    public void Fit_LogoSmallerThanTarget_CentredAtScaleOne()
    {
        PixelRect rect = SplashLayout.Fit(Logo, new PixelSize(1280, 720), margin: 32);

        Assert.Equal(new PixelRect((1280 - 712) / 2, (720 - 168) / 2, 712, 168), rect);
    }

    [Fact]
    public void Fit_TargetSmallerThanLogo_ScalesDownPreservingAspect()
    {
        // 360x260 inside the margins: width-limited, so 360 x floor(360 * 168 / 712) = 360x84.
        PixelRect rect = SplashLayout.Fit(Logo, new PixelSize(400, 300), margin: 20);

        Assert.Equal(new PixelRect(20, (300 - 84) / 2, 360, 84), rect);
    }

    [Fact]
    public void Fit_TallNarrowLogo_IsHeightLimited()
    {
        // 100x50 inside the margins: height-limited, so floor(50 * 40 / 200) x 50 = 10x50.
        PixelRect rect = SplashLayout.Fit(new PixelSize(40, 200), new PixelSize(120, 70), margin: 10);

        Assert.Equal(new PixelRect(55, 10, 10, 50), rect);
    }

    [Fact]
    public void Fit_LogoExactlyFillsSpaceInsideMargins_KeepsItsSize()
    {
        PixelRect rect = SplashLayout.Fit(Logo, new PixelSize(712 + 64, 168 + 64), margin: 32);

        Assert.Equal(new PixelRect(32, 32, 712, 168), rect);
    }

    [Fact]
    public void Fit_MarginLargerThanTarget_ShrinksMarginAndStillFits()
    {
        PixelRect rect = SplashLayout.Fit(Logo, new PixelSize(9, 9), margin: 100);

        // One pixel is left inside the shrunk margins on each axis: 1 x max(1, floor(168 / 712)) = 1x1, centred.
        Assert.Equal(new PixelRect(4, 4, 1, 1), rect);
    }

    [Fact]
    public void Fit_AnySizes_StaysInsideTargetAndKeepsAspect()
    {
        Gen.Select(Gen.Int[1, 4096], Gen.Int[1, 4096], Gen.Int[1, 16384], Gen.Int[1, 16384], Gen.Int[0, 512])
            .Sample((logoWidth, logoHeight, targetWidth, targetHeight, margin) =>
            {
                var logo = new PixelSize(logoWidth, logoHeight);
                var target = new PixelSize(targetWidth, targetHeight);
                PixelRect rect = SplashLayout.Fit(logo, target, margin);
                string context = $"logo {logo}, target {target}, margin {margin}: {rect}";

                // Inside the target, at least one pixel, never upscaled.
                Assert.True(rect.X >= 0 && rect.Y >= 0, context);
                Assert.True(rect.X + rect.Width <= targetWidth && rect.Y + rect.Height <= targetHeight, context);
                Assert.True(rect.Width >= 1 && rect.Height >= 1, context);
                Assert.True(rect.Width <= logoWidth && rect.Height <= logoHeight, context);

                // Centred, to within the half pixel lost to integer division.
                Assert.Equal((targetWidth - rect.Width) / 2, rect.X);
                Assert.Equal((targetHeight - rect.Height) / 2, rect.Y);

                // Same aspect ratio, to within one source pixel of rounding: w/h = lw/lh exactly would make this 0.
                long aspectError = Math.Abs(((long)rect.Width * logoHeight) - ((long)rect.Height * logoWidth));
                Assert.True(aspectError < Math.Max(logoWidth, logoHeight), context);

                // A logo that fits inside the margins keeps its size.
                if (logoWidth + (2L * margin) <= targetWidth && logoHeight + (2L * margin) <= targetHeight)
                {
                    Assert.Equal(logo, rect.Size);
                }
            });
    }

    [Theory]
    [InlineData(0, 168)]
    [InlineData(712, 0)]
    [InlineData(-1, 168)]
    public void Fit_EmptyLogo_Throws(int width, int height)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SplashLayout.Fit(new PixelSize(width, height), new PixelSize(1280, 720), 0));
    }

    [Theory]
    [InlineData(0, 720)]
    [InlineData(1280, 0)]
    [InlineData(1280, -5)]
    public void Fit_ZeroAreaTarget_Throws(int width, int height)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SplashLayout.Fit(Logo, new PixelSize(width, height), 0));
    }

    [Fact]
    public void Fit_NegativeMargin_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SplashLayout.Fit(Logo, new PixelSize(1280, 720), -1));
    }
}
