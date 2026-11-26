using System.Globalization;
using CsCheck;
using Xunit;

namespace BrutalEngine.Platform.Tests;

public sealed class DisplayRectTests
{
    [Fact]
    public void Equality_ComparesEveryField()
    {
        Gen.Select(Gen.Int[-1, 0], Gen.Int[0, 1], Gen.Int[1, 2], Gen.Int[1, 2], Gen.Int[0, 15]).Sample((x, y, width, height, flips) =>
        {
            DisplayRect left = new(x, y, width, height);
            DisplayRect right = new(
                x + (flips & 1),
                y + ((flips >> 1) & 1),
                width + ((flips >> 2) & 1),
                height + ((flips >> 3) & 1));
            bool expected = flips == 0;

            Assert.Equal(expected, left == right);
            Assert.Equal(!expected, left != right);
            if (expected)
            {
                Assert.Equal(left.GetHashCode(), right.GetHashCode());
            }
        });
    }

    [Fact]
    public void ToString_FormatsNegativeCoordinatesInInvariantCulture()
    {
        var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        culture.NumberFormat.NegativeSign = "~";
        CultureInfo previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = culture;
        try
        {
            Assert.Equal(
                "DisplayRect { X = -1920, Y = -40, Width = 1920, Height = 1080 }",
                new DisplayRect(-1920, -40, 1920, 1080).ToString());
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
