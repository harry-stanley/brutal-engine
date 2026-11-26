using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Xunit;

namespace BrutalEngine.Platform.Tests;

public sealed class DisplayInfoTests
{
    private static readonly DisplayInfo _display = new()
    {
        Id = new DisplayId(1),
        Name = "Display",
        Bounds = new DisplayRect(-1920, 0, 1920, 1080),
        UsableBounds = new DisplayRect(-1920, 0, 1920, 1040),
        ContentScale = 1.5f,
        CurrentMode = new DisplayMode
        {
            Display = new DisplayId(1),
            Width = 1920,
            Height = 1080,
            PixelDensity = 1f,
            RefreshRate = 59.94f,
        },
        IsPrimary = true,
    };

    [Fact]
    public void Equality_ComparesEveryProperty()
    {
        DisplayInfo same = _display with { Name = new string("Display".AsSpan()) };

        Assert.Equal(_display, same);
        Assert.True(_display == same);
        Assert.Equal(_display.GetHashCode(), same.GetHashCode());

        Assert.NotEqual(_display, _display with { Id = new DisplayId(2) });
        Assert.NotEqual(_display, _display with { Name = "display" });
        Assert.NotEqual(_display, _display with { Bounds = _display.Bounds with { X = 0 } });
        Assert.NotEqual(_display, _display with { UsableBounds = _display.Bounds });
        Assert.NotEqual(_display, _display with { ContentScale = 1.25f });
        Assert.NotEqual(_display, _display with { CurrentMode = _display.CurrentMode with { RefreshRate = 60f } });
        Assert.NotEqual(_display, _display with { IsPrimary = false });
    }

    [Fact]
    public void RequiredMembers_AreAllButIsPrimary()
    {
        PropertyInfo[] properties = typeof(DisplayInfo).GetProperties();
        string[] required = properties
            .Where(property => property.IsDefined(typeof(RequiredMemberAttribute)))
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["Bounds", "ContentScale", "CurrentMode", "Id", "Name", "UsableBounds"], required);
        Assert.Equal(7, properties.Length);
        Assert.All(properties, property =>
            Assert.Contains(typeof(IsExternalInit), property.SetMethod!.ReturnParameter.GetRequiredCustomModifiers()));
    }

    [Fact]
    public void IsPrimary_DefaultsToFalse()
    {
        DisplayInfo display = new()
        {
            Id = new DisplayId(1),
            Name = "Display",
            Bounds = default,
            UsableBounds = default,
            ContentScale = 1f,
            CurrentMode = default,
        };

        Assert.False(display.IsPrimary);
    }

    [Fact]
    public void ToString_NamesEveryPublicProperty()
    {
        string text = _display.ToString();

        Assert.All(typeof(DisplayInfo).GetProperties(), property =>
            Assert.True(
                text.Contains("{ " + property.Name + " = ", StringComparison.Ordinal)
                    || text.Contains(", " + property.Name + " = ", StringComparison.Ordinal),
                $"ToString leaves out {property.Name}: {text}"));
    }

    [Fact]
    public void ToString_ListsEveryPropertyInInvariantCulture()
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
        try
        {
            Assert.Equal(
                "DisplayInfo { Id = DisplayId { Value = 1 }, Name = Display, "
                + "Bounds = DisplayRect { X = -1920, Y = 0, Width = 1920, Height = 1080 }, "
                + "UsableBounds = DisplayRect { X = -1920, Y = 0, Width = 1920, Height = 1040 }, ContentScale = 1.5, "
                + "CurrentMode = DisplayMode { Display = DisplayId { Value = 1 }, Width = 1920, Height = 1080, "
                + "PixelDensity = 1, RefreshRate = 59.94 }, IsPrimary = True }",
                _display.ToString());
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
