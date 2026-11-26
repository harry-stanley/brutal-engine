using System;
using Xunit;

namespace BrutalEngine.Platform.Sdl3.Tests;

public sealed class ContentSizeModelTests
{
    // SDL's high-DPI README example, a 3840x2160 display at 200 % (docs/design/platform.md section 1): the window's
    // Size, PixelSize, PixelDensity and DisplayScale, and the ContentSize they give.
    [Theory]
    [InlineData("Windows", 3840, 2160, 3840, 2160, 1f, 2f, 1920, 1080)]
    [InlineData("macOS HighPixelDensity", 1920, 1080, 3840, 2160, 2f, 2f, 1920, 1080)]
    [InlineData("macOS default", 1920, 1080, 1920, 1080, 1f, 1f, 1920, 1080)]
    public void ReadmeExample_Table(
        string os, int width, int height, int pixelWidth, int pixelHeight, float density, float scale, int contentWidth, int contentHeight)
    {
        Assert.NotEmpty(os);
        WindowSize content = new(contentWidth, contentHeight);

        Assert.Equal(content, ContentSizeModel.ContentFromPixels(new WindowSize(pixelWidth, pixelHeight), scale));
        Assert.Equal(new WindowSize(width, height), ContentSizeModel.ToWindowCoordinates(content, scale, density));
    }

    [Fact]
    public void Rounding_MidpointAwayFromZero()
    {
        // 2.5 and 0.5 pixels per content unit: banker's rounding would give 2 and 0.
        Assert.Equal(new WindowSize(3, 1), ContentSizeModel.ContentFromPixels(new WindowSize(5, 1), 2f));

        // 4.5 and 7.5 window coordinates: banker's rounding would give 4 and 8, truncation 4 and 7.
        Assert.Equal(new WindowSize(5, 8), ContentSizeModel.ToWindowCoordinates(new WindowSize(3, 5), 1.5f, 1f));

        // 2.5 and 1.5 through the pixel density.
        Assert.Equal(new WindowSize(3, 2), ContentSizeModel.ToWindowCoordinates(new WindowSize(5, 3), 1f, 2f));
    }

    [Fact]
    public void Rounding_NonMidpoint_RoundsToNearest()
    {
        // 666.67 and 667.33: truncation would give 666, rounding up 668.
        Assert.Equal(new WindowSize(667, 667), ContentSizeModel.ContentFromPixels(new WindowSize(1000, 1001), 1.5f));

        // 1600 at 125 %; 1706.67 and 1066.67 at 150 % with a density of 1.5 (content scale 1.0).
        Assert.Equal(new WindowSize(1600, 900), ContentSizeModel.ToWindowCoordinates(new WindowSize(1280, 720), 1.25f, 1f));
        Assert.Equal(new WindowSize(1707, 1067), ContentSizeModel.ToWindowCoordinates(new WindowSize(2560, 1600), 1f, 1.5f));
    }

    [Fact]
    public void ToWindowCoordinates_PositiveSideBelowHalf_IsOne()
    {
        Assert.Equal(new WindowSize(1, 1), ContentSizeModel.ToWindowCoordinates(new WindowSize(1, 1), 1f, 4f));
    }

    [Fact]
    public void ZeroSide_StaysZero()
    {
        // A minimised window can report a zero pixel size.
        Assert.Equal(new WindowSize(0, 0), ContentSizeModel.ContentFromPixels(new WindowSize(0, 0), 2f));
        Assert.Equal(new WindowSize(0, 0), ContentSizeModel.ToWindowCoordinates(new WindowSize(0, 0), 2f, 1f));
    }

    [Theory]
    [InlineData("ToWindow width negative", "content")]
    [InlineData("ToWindow height negative", "content")]
    [InlineData("ToWindow scale zero", "displayScale")]
    [InlineData("ToWindow scale negative", "displayScale")]
    [InlineData("ToWindow scale NaN", "displayScale")]
    [InlineData("ToWindow scale infinite", "displayScale")]
    [InlineData("ToWindow density zero", "pixelDensity")]
    [InlineData("ToWindow density negative", "pixelDensity")]
    [InlineData("ToWindow density NaN", "pixelDensity")]
    [InlineData("ToWindow density infinite", "pixelDensity")]
    [InlineData("ToWindow width overflows", "content")]
    [InlineData("ToWindow height overflows", "content")]
    [InlineData("FromPixels width negative", "pixels")]
    [InlineData("FromPixels height negative", "pixels")]
    [InlineData("FromPixels scale zero", "displayScale")]
    [InlineData("FromPixels scale negative", "displayScale")]
    [InlineData("FromPixels scale NaN", "displayScale")]
    [InlineData("FromPixels scale infinite", "displayScale")]
    [InlineData("FromPixels width overflows", "pixels")]
    public void InvalidArgument_Throws(string row, string paramName)
    {
        WindowSize ok = new(1280, 720);
        Action call = row switch
        {
            "ToWindow width negative" => () => ContentSizeModel.ToWindowCoordinates(new WindowSize(-1, 720), 1f, 1f),
            "ToWindow height negative" => () => ContentSizeModel.ToWindowCoordinates(new WindowSize(1280, int.MinValue), 1f, 1f),
            "ToWindow scale zero" => () => ContentSizeModel.ToWindowCoordinates(ok, 0f, 1f),
            "ToWindow scale negative" => () => ContentSizeModel.ToWindowCoordinates(ok, -2f, 1f),
            "ToWindow scale NaN" => () => ContentSizeModel.ToWindowCoordinates(ok, float.NaN, 1f),
            "ToWindow scale infinite" => () => ContentSizeModel.ToWindowCoordinates(ok, float.PositiveInfinity, 1f),
            "ToWindow density zero" => () => ContentSizeModel.ToWindowCoordinates(ok, 1f, 0f),
            "ToWindow density negative" => () => ContentSizeModel.ToWindowCoordinates(ok, 1f, -1f),
            "ToWindow density NaN" => () => ContentSizeModel.ToWindowCoordinates(ok, 1f, float.NaN),
            "ToWindow density infinite" => () => ContentSizeModel.ToWindowCoordinates(ok, 1f, float.PositiveInfinity),
            "ToWindow width overflows" => () => ContentSizeModel.ToWindowCoordinates(new WindowSize(16384, 1), 131072f, 1f),
            "ToWindow height overflows" => () => ContentSizeModel.ToWindowCoordinates(new WindowSize(1, 16384), 1f, 1f / 131072),
            "FromPixels width negative" => () => ContentSizeModel.ContentFromPixels(new WindowSize(int.MinValue, 720), 1f),
            "FromPixels height negative" => () => ContentSizeModel.ContentFromPixels(new WindowSize(1280, -1), 1f),
            "FromPixels scale zero" => () => ContentSizeModel.ContentFromPixels(ok, 0f),
            "FromPixels scale negative" => () => ContentSizeModel.ContentFromPixels(ok, -1f),
            "FromPixels scale NaN" => () => ContentSizeModel.ContentFromPixels(ok, float.NaN),
            "FromPixels scale infinite" => () => ContentSizeModel.ContentFromPixels(ok, float.PositiveInfinity),
            "FromPixels width overflows" => () => ContentSizeModel.ContentFromPixels(new WindowSize(int.MaxValue, 1), 0.5f),
            _ => throw new ArgumentOutOfRangeException(nameof(row), row, "Unknown row."),
        };

        ArgumentOutOfRangeException exception = Assert.Throws<ArgumentOutOfRangeException>(call);

        Assert.Equal(paramName, exception.ParamName);
    }
}
