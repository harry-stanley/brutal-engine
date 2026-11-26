using System;
using System.Linq;
using Xunit;

namespace BrutalEngine.Platform.Tests;

public sealed class MouseButtonTests
{
    // Written from SDL 3's SDL_BUTTON_* constants (Brutal.Sdl/Generated/Sdl.Constants.gen.cs:27-31), plus None.
    private static readonly (string Name, byte Value)[] _expectedButtons =
    [
        ("None", 0), ("Left", 1), ("Middle", 2), ("Right", 3), ("X1", 4), ("X2", 5),
    ];

    // Written from SDL 3's SDL_MouseButtonFlags (Brutal.Sdl/Generated/Enums/SdlMouseButtonFlags.gen.cs:10-14), plus
    // None.
    private static readonly (string Name, byte Value)[] _expectedFlags =
    [
        ("None", 0), ("Left", 1), ("Middle", 2), ("Right", 4), ("X1", 8), ("X2", 16),
    ];

    [Fact]
    public void UnderlyingTypes_AreByte()
    {
        Assert.Equal(typeof(byte), Enum.GetUnderlyingType(typeof(MouseButton)));
        Assert.Equal(typeof(byte), Enum.GetUnderlyingType(typeof(MouseButtons)));
    }

    [Fact]
    public void MouseButton_NamesAndValues_MatchSdl()
    {
        (string, byte)[] actual =
        [
            .. Enum.GetValues<MouseButton>().Select(button => (Enum.GetName(button)!, (byte)button)),
        ];

        Assert.Equal(_expectedButtons, actual);
    }

    [Fact]
    public void MouseButtons_NamesAndValues_MatchSdl()
    {
        (string, byte)[] actual =
        [
            .. Enum.GetValues<MouseButtons>().Select(buttons => (Enum.GetName(buttons)!, (byte)buttons)),
        ];

        Assert.Equal(_expectedFlags, actual);
    }

    [Theory]
    [InlineData(MouseButton.Left, MouseButtons.Left)]
    [InlineData(MouseButton.Middle, MouseButtons.Middle)]
    [InlineData(MouseButton.Right, MouseButtons.Right)]
    [InlineData(MouseButton.X1, MouseButtons.X1)]
    [InlineData(MouseButton.X2, MouseButtons.X2)]
    public void ButtonFlag_IsOneShiftedByButtonMinusOne(MouseButton button, MouseButtons flag)
    {
        // SDL_BUTTON_MASK(X) is 1u << (X - 1).
        Assert.Equal(1 << ((byte)button - 1), (byte)flag);
    }

    [Fact]
    public void Flags_OnlyOnMouseButtons()
    {
        Assert.False(typeof(MouseButton).IsDefined(typeof(FlagsAttribute), inherit: false));
        Assert.True(typeof(MouseButtons).IsDefined(typeof(FlagsAttribute), inherit: false));
    }

    [Fact]
    public void Default_IsNone()
    {
        Assert.Equal(MouseButton.None, default);
        Assert.Equal(MouseButtons.None, default);
    }
}
