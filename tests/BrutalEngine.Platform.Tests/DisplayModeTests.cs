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
        // DisplayMode compares its floats with float.Equals, so NaN equals NaN: draw NaN for both float fields.
        Gen<DisplayMode> mode =
            Gen.Select(
                    Gen.UInt[0, 1], Gen.Int[0, 1], Gen.Int[0, 1], Gen.OneOf(1f, 2f, float.NaN), Gen.OneOf(0f, 60f, float.NaN))
                .Select((display, width, height, density, refresh) => new DisplayMode
                {
                    Display = new DisplayId(display),
                    Width = width,
                    Height = height,
                    PixelDensity = density,
                    RefreshRate = refresh,
                });

        // Each field of the right mode is the left one's or, if its mask bit is set, another draw: equal pairs, and
        // pairs that differ only in one NaN field, come up often instead of about once in 200 independent draws.
        Gen.Select(mode, mode, Gen.Int[0, 31]).Sample((left, other, redraw) =>
        {
            DisplayMode right = new()
            {
                Display = (redraw & 1) != 0 ? other.Display : left.Display,
                Width = (redraw & 2) != 0 ? other.Width : left.Width,
                Height = (redraw & 4) != 0 ? other.Height : left.Height,
                PixelDensity = (redraw & 8) != 0 ? other.PixelDensity : left.PixelDensity,
                RefreshRate = (redraw & 16) != 0 ? other.RefreshRate : left.RefreshRate,
            };

            bool expected = left.Display.Value == right.Display.Value
                && left.Width == right.Width
                && left.Height == right.Height
                && left.PixelDensity.Equals(right.PixelDensity)
                && left.RefreshRate.Equals(right.RefreshRate);

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
