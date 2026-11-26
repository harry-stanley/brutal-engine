using System;
using System.Collections.Generic;
using System.Globalization;
using Brutal.SdlApi;
using BrutalEngine.Core;
using Xunit;

namespace BrutalEngine.Platform.Sdl3.Tests;

[Collection(SdlNativeTests.Name)]
public sealed class SdlDisplaysTests
{
    // SDL's dummy and offscreen drivers each show one 1024 by 768 display at scale 1 with no fullscreen modes, and
    // name a display without a name by its id (observed on Windows with SDL 3.4.12).
    [Theory]
    [InlineData(HeadlessMode.NoSurface)]
    [InlineData(HeadlessMode.Offscreen)]
    public void Build_OnDummy_ReturnsPrimaryDisplay(HeadlessMode headless)
    {
        using var variable = NativeEnvironmentVariable.Set("SDL_VIDEO_DRIVER", null);
        using SdlVideoSession session = Start(headless);
        DisplayId primary = new(Sdl.PInvoke.GetPrimaryDisplay());

        Result<IReadOnlyList<DisplayInfo>> built = SdlDisplays.Build();

        Assert.True(built.TryGetValue(out IReadOnlyList<DisplayInfo>? displays), built.ToString());
        DisplayInfo display = Assert.Single(displays);
        DisplayInfo expected = new()
        {
            Id = primary,
            Name = primary.Value.ToString(CultureInfo.InvariantCulture),
            Bounds = new DisplayRect(0, 0, 1024, 768),
            UsableBounds = new DisplayRect(0, 0, 1024, 768),
            ContentScale = 1f,
            CurrentMode = new DisplayMode { Display = primary, Width = 1024, Height = 768, PixelDensity = 1f, RefreshRate = 0f },
            IsPrimary = true,
        };
        Assert.Equal(expected, display);
    }

    [Fact]
    public void GetFullscreenModes_OnDummy_IsEmpty()
    {
        using var variable = NativeEnvironmentVariable.Set("SDL_VIDEO_DRIVER", null);
        using SdlVideoSession session = Start(HeadlessMode.NoSurface);

        Result<IReadOnlyList<DisplayMode>> modes = SdlDisplays.GetFullscreenModes(new DisplayId(Sdl.PInvoke.GetPrimaryDisplay()));

        Assert.True(modes.TryGetValue(out IReadOnlyList<DisplayMode>? list), modes.ToString());
        Assert.Empty(list);
    }

    [Theory]
    [InlineData("zero")]
    [InlineData("next id")]
    [InlineData("max")]
    public void GetFullscreenModes_UnknownDisplay_IsNotFound(string row)
    {
        using var variable = NativeEnvironmentVariable.Set("SDL_VIDEO_DRIVER", null);
        using SdlVideoSession session = Start(HeadlessMode.NoSurface);

        // Display ids count up across SDL restarts, so the next id is derived from the primary's.
        uint id = row switch
        {
            "zero" => 0u,
            "next id" => Sdl.PInvoke.GetPrimaryDisplay() + 1,
            _ => uint.MaxValue,
        };

        Result<IReadOnlyList<DisplayMode>> modes = SdlDisplays.GetFullscreenModes(new DisplayId(id));

        Assert.True(modes.TryGetError(out EngineError error));
        Assert.Equal(ErrorKind.NotFound, error.Kind);
        Assert.Equal($"Display {id.ToString(CultureInfo.InvariantCulture)} isn't connected: it may have been unplugged since it was listed.", error.Message);
    }

    [Fact]
    public void Queries_WithoutVideo_AreNative()
    {
        // No session: SDL's video subsystem isn't initialised, so SDL's display queries fail.
        Result<IReadOnlyList<DisplayInfo>> built = SdlDisplays.Build();
        Result<IReadOnlyList<DisplayMode>> modes = SdlDisplays.GetFullscreenModes(new DisplayId(1));

        Assert.True(built.TryGetError(out EngineError buildError));
        Assert.Equal(ErrorKind.Native, buildError.Kind);
        Assert.StartsWith("SDL_GetPrimaryDisplay failed", buildError.Message, StringComparison.Ordinal);
        Assert.True(modes.TryGetError(out EngineError modesError));
        Assert.Equal(ErrorKind.Native, modesError.Kind);
        Assert.StartsWith("SDL_GetDisplays failed", modesError.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ToDisplayMode_CopiesEveryField()
    {
        var mode = new SdlDisplayMode
        {
            displayID = 7,
            format = SdlPixelFormat.Argb8888,
            w = 1920,
            h = 1080,
            pixel_density = 2f,
            refresh_rate = 59.94f,
            refresh_rate_numerator = 60000,
            refresh_rate_denominator = 1001,
        };

        var converted = SdlDisplays.ToDisplayMode(mode);

        Assert.Equal(new DisplayMode { Display = new DisplayId(7), Width = 1920, Height = 1080, PixelDensity = 2f, RefreshRate = 59.94f }, converted);
    }

    // Every value differs from every other one it could be confused with: x != y in each rect, the bounds differ from
    // the usable bounds in every field, the scale isn't 1, and the current mode's display isn't the display's own id.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ToDisplayInfo_CopiesEveryField(bool isPrimary)
    {
        var bounds = new SdlRect { x = -1920, y = 120, w = 1920, h = 1080 };
        var usable = new SdlRect { x = -1900, y = 160, w = 1880, h = 1040 };
        var current = new SdlDisplayMode
        {
            displayID = 11,
            format = SdlPixelFormat.Xrgb8888,
            w = 3840,
            h = 2160,
            pixel_density = 2f,
            refresh_rate = 143.856f,
            refresh_rate_numerator = 144000,
            refresh_rate_denominator = 1001,
        };

        var info = SdlDisplays.ToDisplayInfo(9, "Display nine", bounds, usable, 1.5f, current, isPrimary);

        DisplayInfo expected = new()
        {
            Id = new DisplayId(9),
            Name = "Display nine",
            Bounds = new DisplayRect(-1920, 120, 1920, 1080),
            UsableBounds = new DisplayRect(-1900, 160, 1880, 1040),
            ContentScale = 1.5f,
            CurrentMode = new DisplayMode { Display = new DisplayId(11), Width = 3840, Height = 2160, PixelDensity = 2f, RefreshRate = 143.856f },
            IsPrimary = isPrimary,
        };
        Assert.Equal(expected, info);
    }

    private static SdlVideoSession Start(HeadlessMode headless)
    {
        PlatformOptions options = new() { Organization = "BrutalEngine", Application = "Platform.Sdl3.Tests", Headless = headless };
        return SdlVideoSession.Start(options, null).Value;
    }
}
