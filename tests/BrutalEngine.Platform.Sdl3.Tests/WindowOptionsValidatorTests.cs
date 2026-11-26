using System;
using Xunit;

namespace BrutalEngine.Platform.Sdl3.Tests;

public sealed class WindowOptionsValidatorTests
{
    public static TheoryData<string> InvalidRules =>
    [
        "OptionsNull", "TitleNull",
        "WidthZero", "WidthNegative", "WidthTooLarge", "HeightZero", "HeightNegative", "HeightTooLarge",
        "ModeExclusiveFullscreen", "ModeNegative", "ModeUndefined",
        "SurfaceNegative", "SurfaceUndefined",
        "DisplayDefault",
    ];

    public static TheoryData<string> ValidRules =>
    [
        "Defaults", "TitleEmpty", "SidesMinimum", "SidesMaximum", "BorderlessFullscreen", "NotResizable",
        "NoHighPixelDensity", "SurfaceNone", "DisplaySet", "DisplayLargestId",
    ];

    [Theory]
    [MemberData(nameof(InvalidRules))]
    public void Invalid_Throws(string rule)
    {
        (WindowOptions? options, Type expected, string field, string value) = Invalid(rule);

        Exception exception = Assert.Throws(expected, () => WindowOptionsValidator.Validate(options!));

        Assert.Equal("options", ((ArgumentException)exception).ParamName);
        Assert.Contains(field, exception.Message, StringComparison.Ordinal);
        Assert.Contains(value, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(ValidRules))]
    public void Valid_DoesNotThrow(string rule)
    {
        WindowOptionsValidator.Validate(Valid(rule));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 16385)]
    public void ValidateContentSize_OutOfRange_NamesTheParameter(int width, int height)
    {
        ArgumentOutOfRangeException exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => WindowOptionsValidator.ValidateContentSize(new WindowSize(width, height), "contentSize", "contentSize"));

        Assert.Equal("contentSize", exception.ParamName);
        Assert.Contains(width == 0 ? "contentSize.Width" : "contentSize.Height", exception.Message, StringComparison.Ordinal);
    }

    private static WindowOptions Base => new();

    private static (WindowOptions? Options, Type Expected, string Field, string Value) Invalid(string rule)
    {
        Type argument = typeof(ArgumentException);
        Type range = typeof(ArgumentOutOfRangeException);
        return rule switch
        {
            "OptionsNull" => (null, typeof(ArgumentNullException), "options", "null"),
            "TitleNull" => (Base with { Title = null! }, argument, "WindowOptions.Title", "null"),
            "WidthZero" => (Base with { ContentSize = new(0, 720) }, range, "WindowOptions.ContentSize.Width", "got 0"),
            "WidthNegative" => (Base with { ContentSize = new(int.MinValue, 720) }, range, "WindowOptions.ContentSize.Width", "got -2147483648"),
            "WidthTooLarge" => (Base with { ContentSize = new(16385, 720) }, range, "WindowOptions.ContentSize.Width", "got 16385"),
            "HeightZero" => (Base with { ContentSize = new(1280, 0) }, range, "WindowOptions.ContentSize.Height", "got 0"),
            "HeightNegative" => (Base with { ContentSize = new(1280, -1) }, range, "WindowOptions.ContentSize.Height", "got -1"),
            "HeightTooLarge" => (Base with { ContentSize = new(1280, 16385) }, range, "WindowOptions.ContentSize.Height", "got 16385"),
            "ModeExclusiveFullscreen" => (Base with { Mode = WindowMode.ExclusiveFullscreen }, argument, "WindowOptions.Mode", "ExclusiveFullscreen"),
            "ModeNegative" => (Base with { Mode = (WindowMode)(-1) }, range, "WindowOptions.Mode", "got -1"),
            "ModeUndefined" => (Base with { Mode = (WindowMode)3 }, range, "WindowOptions.Mode", "got 3"),
            "SurfaceNegative" => (Base with { Surface = (WindowSurface)int.MinValue }, range, "WindowOptions.Surface", "got -2147483648"),
            "SurfaceUndefined" => (Base with { Surface = (WindowSurface)2 }, range, "WindowOptions.Surface", "got 2"),
            "DisplayDefault" => (Base with { Display = default(DisplayId) }, argument, "WindowOptions.Display", "default(DisplayId)"),
            _ => throw new ArgumentOutOfRangeException(nameof(rule), rule, "Unknown rule."),
        };
    }

    private static WindowOptions Valid(string rule) => rule switch
    {
        "Defaults" => Base,
        "TitleEmpty" => Base with { Title = "" },
        "SidesMinimum" => Base with { ContentSize = new(1, 1) },
        "SidesMaximum" => Base with { ContentSize = new(16384, 16384) },
        "BorderlessFullscreen" => Base with { Mode = WindowMode.BorderlessFullscreen },
        "NotResizable" => Base with { Resizable = false },
        "NoHighPixelDensity" => Base with { HighPixelDensity = false },
        "SurfaceNone" => Base with { Surface = WindowSurface.None },
        "DisplaySet" => Base with { Display = new DisplayId(1) },
        "DisplayLargestId" => Base with { Display = new DisplayId(uint.MaxValue) },
        _ => throw new ArgumentOutOfRangeException(nameof(rule), rule, "Unknown rule."),
    };
}
