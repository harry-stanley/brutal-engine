using System;
using HelloWindow.Splash;
using Xunit;

namespace HelloWindow.Tests;

public sealed class LogoCompositorTests
{
    private static Rgb8 Grey => new(10, 20, 30);

    // One pixel per channel value 0..255, cycling the channels so red, green and blue each see every value.
    private static byte[] Pixels(byte alpha)
    {
        byte[] rgba = new byte[256 * 4];
        for (int i = 0; i < 256; i++)
        {
            rgba[(i * 4) + 0] = (byte)i;
            rgba[(i * 4) + 1] = (byte)(255 - i);
            rgba[(i * 4) + 2] = (byte)(i * 7);
            rgba[(i * 4) + 3] = alpha;
        }

        return rgba;
    }

    [Fact]
    public void Composite_OpaquePixels_Unchanged()
    {
        byte[] rgba = Pixels(alpha: 255);
        byte[] destination = new byte[rgba.Length];

        LogoCompositor.Composite(rgba, destination, Grey, LogoPixelOrder.Rgba);

        Assert.Equal(rgba, destination);
    }

    [Fact]
    public void Composite_TransparentPixels_BecomeBackground()
    {
        byte[] rgba = Pixels(alpha: 0);
        byte[] destination = new byte[rgba.Length];

        LogoCompositor.Composite(rgba, destination, Grey, LogoPixelOrder.Rgba);

        for (int i = 0; i < destination.Length; i += 4)
        {
            Assert.Equal([Grey.R, Grey.G, Grey.B, 255], destination[i..(i + 4)]);
        }
    }

    [Fact]
    public void Composite_PartialAlpha_BlendsAndRounds()
    {
        // 200 at alpha 128 over 10: (200 * 128 + 10 * 127) / 255 = 105.38 -> 105. Over black: 25600 / 255 = 100.39 -> 100.
        byte[] rgba = [200, 200, 200, 128];
        byte[] overGrey = new byte[4];
        byte[] overBlack = new byte[4];

        LogoCompositor.Composite(rgba, overGrey, new Rgb8(10, 10, 10), LogoPixelOrder.Rgba);
        LogoCompositor.Composite(rgba, overBlack, Rgb8.Black, LogoPixelOrder.Rgba);

        Assert.Equal([105, 105, 105, 255], overGrey);
        Assert.Equal([100, 100, 100, 255], overBlack);
    }

    [Fact]
    public void Composite_FractionAboveHalf_RoundsUp()
    {
        // 200 at alpha 192 over black: 200 * 192 / 255 = 150.59, so round-to-nearest gives 151 where truncation gives 150.
        // Green 100 over 30: (100 * 192 + 30 * 63) / 255 = 82.71 -> 83.
        byte[] rgba = [200, 100, 200, 192];
        byte[] destination = new byte[4];

        LogoCompositor.Composite(rgba, destination, new Rgb8(0, 30, 0), LogoPixelOrder.Rgba);

        Assert.Equal([151, 83, 151, 255], destination);
    }

    [Fact]
    public void Composite_PartialAlpha_OutputIsOpaque()
    {
        byte[] rgba = Pixels(alpha: 77);
        byte[] destination = new byte[rgba.Length];

        LogoCompositor.Composite(rgba, destination, Grey, LogoPixelOrder.Rgba);

        for (int i = 3; i < destination.Length; i += 4)
        {
            Assert.Equal(255, destination[i]);
        }
    }

    [Fact]
    public void Composite_BgraOrder_SwapsRedAndBlue()
    {
        byte[] rgba = [1, 2, 3, 255, 40, 50, 60, 0];
        byte[] destination = new byte[rgba.Length];

        LogoCompositor.Composite(rgba, destination, new Rgb8(7, 8, 9), LogoPixelOrder.Bgra);

        Assert.Equal([3, 2, 1, 255, 9, 8, 7, 255], destination);
    }

    [Fact]
    public void Composite_Empty_DoesNothing()
    {
        LogoCompositor.Composite([], [], Grey, LogoPixelOrder.Rgba);
    }

    [Fact]
    public void Composite_PartialPixel_Throws()
    {
        Assert.Throws<ArgumentException>(() => LogoCompositor.Composite(new byte[6], new byte[6], Grey, LogoPixelOrder.Rgba));
    }

    [Fact]
    public void Composite_DestinationLengthDiffers_Throws()
    {
        Assert.Throws<ArgumentException>(() => LogoCompositor.Composite(new byte[8], new byte[4], Grey, LogoPixelOrder.Rgba));
    }

    [Fact]
    public void Composite_UndefinedOrder_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LogoCompositor.Composite(new byte[4], new byte[4], Grey, (LogoPixelOrder)2));
    }
}
