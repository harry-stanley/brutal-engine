using System;
using Brutal.SdlApi;
using CsCheck;
using Xunit;

namespace BrutalEngine.Platform.Sdl3.Tests;

public sealed class FullscreenModeMatcherTests
{
    private const SdlPixelFormat Xrgb = SdlPixelFormat.Xrgb8888;
    private const SdlPixelFormat Argb = SdlPixelFormat.Argb8888;

    // One display's modes in SDL's order, with format duplicates (0 and 1, 5 and 6), near misses and a mode of unknown
    // refresh rate. Every value is literal.
    private static readonly SdlDisplayMode[] _modes =
    [
        Mode(2560, 1440, 1f, 144f, Xrgb),
        Mode(2560, 1440, 1f, 144f, Argb),
        Mode(2560, 1440, 1f, 59.94f, Xrgb),
        Mode(2560, 1440, 1f, 60f, Xrgb),
        Mode(1920, 1080, 2f, 60f, Xrgb),
        Mode(1920, 1080, 1f, 60f, Argb),
        Mode(1920, 1080, 1f, 60f, Xrgb),
        Mode(1080, 1920, 1f, 60f, Xrgb),
        Mode(800, 600, 1f, 0f, Xrgb),
    ];

    [Theory]
    [InlineData(2560, 1440, 1f, 144f, 0)] // the first of two format duplicates
    [InlineData(2560, 1440, 1f, 59.94f, 2)]
    [InlineData(2560, 1440, 1f, 60f, 3)]
    [InlineData(1920, 1080, 2f, 60f, 4)] // pixel density tells it from 5
    [InlineData(1920, 1080, 1f, 60f, 5)] // the first of a later pair of duplicates
    [InlineData(1080, 1920, 1f, 60f, 7)] // width and height aren't interchangeable
    [InlineData(800, 600, 1f, 0f, 8)] // an unknown refresh rate is matched as 0
    [InlineData(2560, 1440, 1f, 60.000004f, -1)] // one float step above 60: no tolerance
    [InlineData(2560, 1440, 1f, 59.999996f, -1)] // one float step below 60
    [InlineData(2560, 1440, 1f, 59.9399f, -1)]
    [InlineData(2560, 1440, 1f, 143.99f, -1)]
    [InlineData(1920, 1080, 1.0000001f, 60f, -1)] // one float step above density 1
    [InlineData(1920, 1080, 1.5f, 60f, -1)]
    [InlineData(2561, 1440, 1f, 144f, -1)]
    [InlineData(2560, 1439, 1f, 144f, -1)]
    [InlineData(1440, 2560, 1f, 144f, -1)]
    [InlineData(800, 600, 1f, 60f, -1)] // 60 doesn't match an unknown refresh rate
    [InlineData(1920, 1080, 1f, float.NaN, -1)]
    [InlineData(0, 0, 0f, 0f, -1)] // default(DisplayMode)'s fields
    public void ExactMatch_Table(int width, int height, float pixelDensity, float refreshRate, int expected)
    {
        DisplayMode wanted = new() { Display = new DisplayId(1), Width = width, Height = height, PixelDensity = pixelDensity, RefreshRate = refreshRate };

        Assert.Equal(expected, FullscreenModeMatcher.FindExact(_modes, wanted));
    }

    [Fact]
    public void FindExact_EmptyList_IsMinusOne()
    {
        DisplayMode wanted = new() { Display = new DisplayId(1), Width = 1920, Height = 1080, PixelDensity = 1f, RefreshRate = 60f };

        Assert.Equal(-1, FullscreenModeMatcher.FindExact([], wanted));
    }

    [Fact]
    public void FindExact_OtherDisplayId_StillMatches()
    {
        // The list is already the wanted display's, so the display id isn't compared.
        DisplayMode wanted = new() { Display = new DisplayId(99), Width = 2560, Height = 1440, PixelDensity = 1f, RefreshRate = 60f };

        Assert.Equal(3, FullscreenModeMatcher.FindExact(_modes, wanted));
    }

    [Fact]
    public void FindExact_RandomLists_ReturnsFirstExactMatch()
    {
        // Small domains, so lists hold many duplicates and near misses (a float step apart).
        var mode = Gen.Select(
            Gen.OneOf(640, 1280, 1920), Gen.OneOf(480, 720, 1080), Gen.OneOf(1f, 1.5f, 2f),
            Gen.OneOf(0f, 59.94f, 60f, 60.000004f, 144f), Gen.OneOf(Xrgb, Argb),
            (width, height, density, refresh, format) => Mode(width, height, density, refresh, format));

        Gen.Select(mode.Array[0, 24], mode, Gen.Int[0, 30]).Sample((modes, other, pick) =>
        {
            // Half the time the wanted mode is one from the list, otherwise anything from the domain.
            SdlDisplayMode source = pick < modes.Length ? modes[pick] : other;
            DisplayMode wanted = new() { Display = new DisplayId(1), Width = source.w, Height = source.h, PixelDensity = source.pixel_density, RefreshRate = source.refresh_rate };

            int expected = Array.FindIndex(
                modes, m => (m.w, m.h, m.pixel_density, m.refresh_rate) == (wanted.Width, wanted.Height, wanted.PixelDensity, wanted.RefreshRate));

            Assert.Equal(expected, FullscreenModeMatcher.FindExact(modes, wanted));
        });
    }

    private static SdlDisplayMode Mode(int width, int height, float pixelDensity, float refreshRate, SdlPixelFormat format) => new()
    {
        displayID = 1,
        format = format,
        w = width,
        h = height,
        pixel_density = pixelDensity,
        refresh_rate = refreshRate,
    };
}
