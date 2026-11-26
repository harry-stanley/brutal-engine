using System;
using System.Linq;
using Xunit;

namespace BrutalEngine.Platform.Tests;

public sealed class KeyModifiersTests
{
    // Every member's name and value, in value order, written from SDL 3's SDL_KMOD_* constants
    // (Brutal.Sdl/Generated/Sdl.Constants.gen.cs:386-403).
    private static readonly (string Name, ushort Value)[] _expected =
    [
        ("None", 0), ("LeftShift", 1), ("RightShift", 2), ("Shift", 3), ("Level5", 4), ("LeftControl", 64),
        ("RightControl", 128), ("Control", 192), ("LeftAlt", 256), ("RightAlt", 512), ("Alt", 768),
        ("LeftGui", 1024), ("RightGui", 2048), ("Gui", 3072), ("Num", 4096), ("Caps", 8192), ("Mode", 16384),
        ("Scroll", 32768),
    ];

    [Theory]
    [InlineData(KeyModifiers.None, 0u)] // KmodNone, :396
    [InlineData(KeyModifiers.LeftShift, 1u)] // KmodLshift, :394
    [InlineData(KeyModifiers.RightShift, 2u)] // KmodRshift, :401
    [InlineData(KeyModifiers.Level5, 4u)] // KmodLevel5, :392
    [InlineData(KeyModifiers.LeftControl, 64u)] // KmodLctrl, :391
    [InlineData(KeyModifiers.RightControl, 128u)] // KmodRctrl, :399
    [InlineData(KeyModifiers.LeftAlt, 256u)] // KmodLalt, :390
    [InlineData(KeyModifiers.RightAlt, 512u)] // KmodRalt, :398
    [InlineData(KeyModifiers.LeftGui, 1024u)] // KmodLgui, :393
    [InlineData(KeyModifiers.RightGui, 2048u)] // KmodRgui, :400
    [InlineData(KeyModifiers.Num, 4096u)] // KmodNum, :397
    [InlineData(KeyModifiers.Caps, 8192u)] // KmodCaps, :387
    [InlineData(KeyModifiers.Mode, 16384u)] // KmodMode, :395
    [InlineData(KeyModifiers.Scroll, 32768u)] // KmodScroll, :402
    [InlineData(KeyModifiers.Shift, 3u)] // KmodShift, :403
    [InlineData(KeyModifiers.Control, 192u)] // KmodCtrl, :388
    [InlineData(KeyModifiers.Alt, 768u)] // KmodAlt, :386
    [InlineData(KeyModifiers.Gui, 3072u)] // KmodGui, :389
    public void Values_MatchSdlBits(KeyModifiers modifier, uint sdlValue)
    {
        Assert.Equal(sdlValue, (uint)modifier);
    }

    [Fact]
    public void NamesAndValues_MatchSdlTable()
    {
        (string, ushort)[] actual =
        [
            .. Enum.GetValues<KeyModifiers>().Select(modifier => (Enum.GetName(modifier)!, (ushort)modifier)),
        ];

        Assert.Equal(_expected, actual);
    }

    [Theory]
    [InlineData(KeyModifiers.Shift, KeyModifiers.LeftShift, KeyModifiers.RightShift)]
    [InlineData(KeyModifiers.Control, KeyModifiers.LeftControl, KeyModifiers.RightControl)]
    [InlineData(KeyModifiers.Alt, KeyModifiers.LeftAlt, KeyModifiers.RightAlt)]
    [InlineData(KeyModifiers.Gui, KeyModifiers.LeftGui, KeyModifiers.RightGui)]
    public void BothSidesCombination_IsLeftOrRight(KeyModifiers both, KeyModifiers left, KeyModifiers right)
    {
        Assert.Equal(left | right, both);
    }

    [Fact]
    public void SingleKeyMembers_AreDistinctBits()
    {
        KeyModifiers[] single =
        [
            KeyModifiers.LeftShift, KeyModifiers.RightShift, KeyModifiers.Level5, KeyModifiers.LeftControl,
            KeyModifiers.RightControl, KeyModifiers.LeftAlt, KeyModifiers.RightAlt, KeyModifiers.LeftGui,
            KeyModifiers.RightGui, KeyModifiers.Num, KeyModifiers.Caps, KeyModifiers.Mode, KeyModifiers.Scroll,
        ];

        Assert.All(single, modifier => Assert.Equal(1, ushort.PopCount((ushort)modifier)));
        Assert.Equal(single.Length, single.Distinct().Count());
    }

    [Fact]
    public void Type_IsUInt16Flags()
    {
        Assert.Equal(typeof(ushort), Enum.GetUnderlyingType(typeof(KeyModifiers)));
        Assert.True(typeof(KeyModifiers).IsDefined(typeof(FlagsAttribute), inherit: false));
    }
}
