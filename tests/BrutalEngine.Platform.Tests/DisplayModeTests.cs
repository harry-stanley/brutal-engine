using System.Globalization;
using CsCheck;
using Xunit;

namespace BrutalEngine.Platform.Tests;

public sealed class DisplayModeTests
{
    private static readonly DisplayMode _mode = new()
    {
        Display = new DisplayId(2),
        Width = 1920,
        Height = 1080,
        PixelDensity = 2f,
        RefreshRate = 59.94f,
    };

    [Fact]
    public void Equality_ComparesEveryField()
    {
        DisplayMode same = _mode with { };

        Assert.Equal(_mode, same);
        Assert.True(_mode == same);
        Assert.Equal(_mode.GetHashCode(), same.GetHashCode());

        Assert.NotEqual(_mode, _mode with { Display = new DisplayId(3) });
        Assert.NotEqual(_mode, _mode with { Width = 1919 });
        Assert.NotEqual(_mode, _mode with { Height = 1079 });
        Assert.NotEqual(_mode, _mode with { PixelDensity = 1f });
        Assert.NotEqual(_mode, _mode with { RefreshRate = 60f });
    }

    [Fact]
    public void Equality_MatchesFieldwiseComparison()
    {
        Gen<DisplayMode> mode =
            Gen.Select(Gen.UInt[0, 1], Gen.Int[0, 1], Gen.Int[0, 1], Gen.Int[1, 2], Gen.Int[0, 1])
                .Select((display, width, height, density, refresh) => new DisplayMode
                {
                    Display = new DisplayId(display),
                    Width = width,
                    Height = height,
                    PixelDensity = density,
                    RefreshRate = refresh * 60f,
                });

        Gen.Select(mode, mode).Sample((left, right) =>
        {
            bool expected = left.Display.Value == right.Display.Value
                && left.Width == right.Width
                && left.Height == right.Height
                && left.PixelDensity == right.PixelDensity
                && left.RefreshRate == right.RefreshRate;

            Assert.Equal(expected, left == right);
            Assert.Equal(!expected, left != right);
            if (expected)
            {
                Assert.Equal(left.GetHashCode(), right.GetHashCode());
            }
        });
    }

    [Fact]
    public void Default_HasDisplayZeroAndNoSize()
    {
        DisplayMode mode = default;

        Assert.Equal(default, mode.Display);
        Assert.Equal(0, mode.Width);
        Assert.Equal(0, mode.Height);
        Assert.Equal(0f, mode.PixelDensity);
        Assert.Equal(0f, mode.RefreshRate);
    }

    [Fact]
    public void ToString_ListsEveryFieldInInvariantCulture()
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
        try
        {
            Assert.Equal(
                "DisplayMode { Display = DisplayId { Value = 2 }, Width = 1920, Height = 1080, PixelDensity = 2, RefreshRate = 59.94 }",
                _mode.ToString());
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
