using System;
using System.Text;
using CsCheck;
using Xunit;

namespace BrutalEngine.Platform.Tests;

public sealed class KeycodeTests
{
    private const uint ScancodeMask = 0x40000000;

    [Fact]
    public void FromScancode_SetsBit30()
    {
        // SDL's own value for SDLK_F1 (Brutal.Sdl/Generated/Enums/SdlKeycode.gen.cs:75).
        Assert.Equal(1073741882u, Keycode.FromScancode(Scancode.F1).Value);
        Assert.Equal(ScancodeMask, Keycode.FromScancode(Scancode.Unknown).Value);
    }

    [Fact]
    public void FromScancode_KeepsTheScancodeInTheLowBits()
    {
        Gen.UShort.Sample(value =>
        {
            var keycode = Keycode.FromScancode((Scancode)value);

            Assert.Equal(value | ScancodeMask, keycode.Value);
            Assert.False(keycode.IsCharacter);
        });
    }

    [Fact]
    public void Constructor_StoresValue()
    {
        Assert.Equal(uint.MaxValue, new Keycode(uint.MaxValue).Value);
        Assert.Equal(0u, default(Keycode).Value);
    }

    [Fact]
    public void Character_ForLetter_ReturnsRune()
    {
        // SDL's own value for SDLK_A (Brutal.Sdl/Generated/Enums/SdlKeycode.gen.cs:19).
        Keycode keycode = new(97);

        Assert.True(keycode.IsCharacter);
        Assert.Equal(new Rune('a'), keycode.Character);
    }

    [Fact]
    public void Character_ForSupplementaryCodePoint_ReturnsRune()
    {
        Keycode keycode = new(0x1F600);

        Assert.True(keycode.IsCharacter);
        Assert.Equal(new Rune(0x1F600), keycode.Character);
    }

    [Fact]
    public void Character_ForScancodeKey_Throws()
    {
        var keycode = Keycode.FromScancode(Scancode.F1);

        Assert.False(keycode.IsCharacter);
        Assert.Throws<InvalidOperationException>(() => keycode.Character);
    }

    [Fact]
    public void IsCharacter_ForSurrogate_False()
    {
        Assert.False(new Keycode(0xD800).IsCharacter);
        Assert.False(new Keycode(0xDFFF).IsCharacter);
    }

    [Fact]
    public void Character_ForSurrogate_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => new Keycode(0xDC00).Character);
    }

    [Fact]
    public void IsCharacter_ForUnknownKey_False()
    {
        // 0 is SDL's unknown key (SDLK_UNKNOWN), not a typed NUL.
        Keycode[] unknownKeys = [default, new(0)];

        foreach (Keycode keycode in unknownKeys)
        {
            Assert.False(keycode.IsCharacter);
            Assert.Throws<InvalidOperationException>(() => keycode.Character);
        }
    }

    [Fact]
    public void IsCharacter_AboveUnicodeRange_False()
    {
        Assert.False(new Keycode(0x110000).IsCharacter);
        Assert.Throws<InvalidOperationException>(() => new Keycode(0x110000).Character);
    }

    [Fact]
    public void IsCharacter_MatchesRuneIsValid()
    {
        // Most samples are around the surrogate block or within the Unicode range, so both outcomes and both kinds
        // of invalid value below 0x110000 are well covered. The zero range keeps the unknown key in every run.
        var value = Gen.OneOf(Gen.UInt[0xD700, 0xE100], Gen.UInt[0, 0x11_0100], Gen.UInt[0, 2], Gen.UInt);

        value.Sample(raw =>
        {
            Keycode keycode = new(raw);
            bool expected = raw != 0 && (raw & ScancodeMask) == 0 && Rune.IsValid(raw);

            Assert.Equal(expected, keycode.IsCharacter);
            if (expected)
            {
                Assert.Equal(raw, (uint)keycode.Character.Value);
            }
        });
    }

    [Fact]
    public void Equality_ComparesValue()
    {
        Gen.Select(Gen.UInt[0, 3], Gen.UInt[0, 3]).Sample((a, b) =>
        {
            Keycode left = new(a);
            Keycode right = new(b);

            Assert.Equal(a == b, left == right);
            Assert.Equal(a != b, left != right);
            Assert.Equal(a == b, left.Equals((object)right));
            if (a == b)
            {
                Assert.Equal(left.GetHashCode(), right.GetHashCode());
            }
        });
    }

    [Fact]
    public void ToString_ForScancodeKey_FormatsValueWithoutThrowing()
    {
        Assert.Equal("Keycode { Value = 1073741882 }", Keycode.FromScancode(Scancode.F1).ToString());
        Assert.Equal("Keycode { Value = 97 }", new Keycode(97).ToString());
    }
}
