using System;
using System.Linq;
using Xunit;

namespace BrutalEngine.Platform.Tests;

public sealed class WindowOptionsTests
{
    private static readonly WindowOptions _defaults = new();

    [Fact]
    public void Defaults_MatchDesign()
    {
        Assert.Equal("BrutalEngine", _defaults.Title);
        Assert.Equal(new WindowSize(1280, 720), _defaults.ContentSize);
        Assert.Equal(WindowMode.Windowed, _defaults.Mode);
        Assert.True(_defaults.Resizable);
        Assert.True(_defaults.HighPixelDensity);
        Assert.Equal(WindowSurface.Vulkan, _defaults.Surface);
        Assert.Null(_defaults.Display);
        Assert.Equal(
            ["ContentSize", "Display", "HighPixelDensity", "Mode", "Resizable", "Surface", "Title"],
            typeof(WindowOptions).GetProperties().Select(property => property.Name).Order(StringComparer.Ordinal));
    }

    public static TheoryData<string> PropertyNames =>
        ["Title", "ContentSize", "Mode", "Resizable", "HighPixelDensity", "Surface", "Display"];

    [Theory]
    [MemberData(nameof(PropertyNames))]
    public void With_ChangesOnlyNamedProperty(string name)
    {
        WindowOptions changed = name switch
        {
            "Title" => _defaults with { Title = "Other" },
            "ContentSize" => _defaults with { ContentSize = new WindowSize(640, 480) },
            "Mode" => _defaults with { Mode = WindowMode.BorderlessFullscreen },
            "Resizable" => _defaults with { Resizable = false },
            "HighPixelDensity" => _defaults with { HighPixelDensity = false },
            "Surface" => _defaults with { Surface = WindowSurface.None },
            "Display" => _defaults with { Display = new DisplayId(2) },
            _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown property."),
        };

        Assert.NotEqual(_defaults, changed);
        Assert.Equal(name == "Title" ? "Other" : "BrutalEngine", changed.Title);
        Assert.Equal(name == "ContentSize" ? new WindowSize(640, 480) : new WindowSize(1280, 720), changed.ContentSize);
        Assert.Equal(name == "Mode" ? WindowMode.BorderlessFullscreen : WindowMode.Windowed, changed.Mode);
        Assert.Equal(name != "Resizable", changed.Resizable);
        Assert.Equal(name != "HighPixelDensity", changed.HighPixelDensity);
        Assert.Equal(name == "Surface" ? WindowSurface.None : WindowSurface.Vulkan, changed.Surface);
        Assert.Equal(name == "Display" ? new DisplayId(2) : (DisplayId?)null, changed.Display);
        Assert.Equal(new WindowOptions(), _defaults);
    }
}
