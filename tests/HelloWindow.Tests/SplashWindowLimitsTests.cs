using System;
using CsCheck;
using HelloWindow.Splash;
using Xunit;

namespace HelloWindow.Tests;

public sealed class SplashWindowLimitsTests
{
    private static PixelSize Logo => new(712, 168);

    [Fact]
    public void MinimumWindowSize_Blit_IsNoLimit()
    {
        Assert.Equal(default, SplashWindowLimits.MinimumWindowSize(SplashTransfer.Blit, Logo, 32, 1.0f));
    }

    [Fact]
    public void MinimumWindowSize_CopyAtDensityOne_IsLogoPlusMargins()
    {
        Assert.Equal(new PixelSize(712 + 64, 168 + 64), SplashWindowLimits.MinimumWindowSize(SplashTransfer.Copy, Logo, 32, 1.0f));
    }

    [Theory]
    [InlineData(2.0f, 388, 116)]
    [InlineData(1.5f, 518, 155)]
    public void MinimumWindowSize_CopyAtHighDensity_RoundsWindowCoordinatesUp(float density, int width, int height)
    {
        // 776x232 pixels: 776 / 1.5 = 517.3 and 232 / 1.5 = 154.7 round up, so the window never falls short.
        Assert.Equal(new PixelSize(width, height), SplashWindowLimits.MinimumWindowSize(SplashTransfer.Copy, Logo, 32, density));
    }

    [Fact]
    public void MinimumWindowSize_Copy_KeepsLogoUnscaled()
    {
        float[] densities = [1.0f, 1.25f, 1.5f, 1.75f, 2.0f, 3.0f];
        Gen.Select(Gen.Int[1, 2048], Gen.Int[1, 2048], Gen.Int[0, 128], Gen.Int[0, densities.Length - 1])
            .Sample((width, height, margin, densityIndex) =>
            {
                var logo = new PixelSize(width, height);
                float density = densities[densityIndex];
                PixelSize minimum = SplashWindowLimits.MinimumWindowSize(SplashTransfer.Copy, logo, margin, density);

                // The smallest window allowed still holds the logo unscaled inside the margins, so a copy always works...
                var pixels = new PixelSize((int)Math.Floor(minimum.Width * (double)density), (int)Math.Floor(minimum.Height * (double)density));
                Assert.Equal(logo, SplashLayout.Fit(logo, pixels, margin).Size);

                // ...and it is the smallest such window.
                Assert.True((minimum.Width - 1) * (double)density < width + (2.0 * margin), $"{logo}, {margin}, {density}: {minimum}");
                Assert.True((minimum.Height - 1) * (double)density < height + (2.0 * margin), $"{logo}, {margin}, {density}: {minimum}");
            });
    }

    [Fact]
    public void TryUpdate_FirstCopy_SetsTheMinimum()
    {
        PixelSize applied = default;

        Assert.True(SplashWindowLimits.TryUpdate(ref applied, SplashTransfer.Copy, Logo, 32, 1.0f));

        Assert.Equal(new PixelSize(776, 232), applied);
    }

    [Fact]
    public void TryUpdate_SameDensity_KeepsTheMinimum()
    {
        // A resize at the same density needs no new SDL call.
        var applied = new PixelSize(388, 116);

        Assert.False(SplashWindowLimits.TryUpdate(ref applied, SplashTransfer.Copy, Logo, 32, 2.0f));

        Assert.Equal(new PixelSize(388, 116), applied);
    }

    [Fact]
    public void TryUpdate_LowerDensity_RaisesTheMinimum()
    {
        // The window moved from a 2x display to a 1x one during the splash: the same window coordinates now hold half
        // the pixels, so the minimum must double to keep the logo and its margins.
        var applied = new PixelSize(388, 116);

        Assert.True(SplashWindowLimits.TryUpdate(ref applied, SplashTransfer.Copy, Logo, 32, 1.0f));

        Assert.Equal(new PixelSize(776, 232), applied);
    }

    [Fact]
    public void TryUpdate_HigherDensity_LowersTheMinimum()
    {
        var applied = new PixelSize(776, 232);

        Assert.True(SplashWindowLimits.TryUpdate(ref applied, SplashTransfer.Copy, Logo, 32, 1.5f));

        Assert.Equal(new PixelSize(518, 155), applied);
    }

    [Fact]
    public void TryUpdate_Blit_SetsNoLimit()
    {
        PixelSize applied = default;

        Assert.False(SplashWindowLimits.TryUpdate(ref applied, SplashTransfer.Blit, Logo, 32, 1.0f));

        Assert.Equal(default, applied);
    }

    [Fact]
    public void TryUpdate_InvalidDensity_ThrowsAndKeepsTheMinimum()
    {
        var applied = new PixelSize(776, 232);

        Assert.Throws<ArgumentOutOfRangeException>(() => SplashWindowLimits.TryUpdate(ref applied, SplashTransfer.Copy, Logo, 32, 0.0f));

        Assert.Equal(new PixelSize(776, 232), applied);
    }

    [Fact]
    public void MinimumWindowSize_Unsupported_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SplashWindowLimits.MinimumWindowSize(SplashTransfer.Unsupported, Logo, 32, 1.0f));
    }

    [Theory]
    [InlineData(0.0f)]
    [InlineData(-1.0f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void MinimumWindowSize_InvalidDensity_Throws(float density)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SplashWindowLimits.MinimumWindowSize(SplashTransfer.Copy, Logo, 32, density));
    }

    [Fact]
    public void MinimumWindowSize_NegativeMargin_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SplashWindowLimits.MinimumWindowSize(SplashTransfer.Copy, Logo, -1, 1.0f));
    }

    [Fact]
    public void MinimumWindowSize_EmptyLogo_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SplashWindowLimits.MinimumWindowSize(SplashTransfer.Copy, new PixelSize(0, 168), 32, 1.0f));
    }
}
