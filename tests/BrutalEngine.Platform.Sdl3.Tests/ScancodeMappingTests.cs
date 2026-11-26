using System;
using Brutal.SdlApi;
using Xunit;

namespace BrutalEngine.Platform.Sdl3.Tests;

// Scancode is SDL's numbering (docs/design/platform.md, "Keys"). These compare it with BRUTAL's generated SdlScancode
// (Brutal.Sdl/Generated/Enums/SdlScancode.gen.cs), the independent source, and check that translation keeps it.
public sealed class ScancodeMappingTests
{
    // SdlScancode members that are not keys: SDL_SCANCODE_RESERVED (400) and SDL_SCANCODE_COUNT (512).
    private static readonly string[] _sdlOnly = ["Reserved", "Count"];

    [Fact]
    public void EveryMember_EqualsSdlMemberOfSameName()
    {
        string[] sdlNames = Enum.GetNames<SdlScancode>();
        foreach (string name in Enum.GetNames<Scancode>())
        {
            Assert.True(Array.IndexOf(sdlNames, name) >= 0, $"SdlScancode has no member {name}.");
            Assert.Equal((uint)Enum.Parse<SdlScancode>(name), (uint)Enum.Parse<Scancode>(name));
        }
    }

    [Fact]
    public void EverySdlKey_HasMemberOfSameName()
    {
        string[] names = Enum.GetNames<Scancode>();
        foreach (string name in Enum.GetNames<SdlScancode>())
        {
            Assert.True(Array.IndexOf(_sdlOnly, name) >= 0 || Array.IndexOf(names, name) >= 0, $"Scancode has no member {name}.");
        }

        Assert.Equal(Enum.GetNames<SdlScancode>().Length - _sdlOnly.Length, names.Length);
    }

    [Fact]
    public void Translate_EverySdlScancode_KeepsValueAndName()
    {
        // Through the translator, so a mapping it swaps (rather than casts) fails here.
        foreach (SdlScancode sdl in Enum.GetValues<SdlScancode>())
        {
            Scancode translated = TranslateKey(sdl);

            Assert.Equal((uint)sdl, (uint)translated);
            string name = Enum.GetName(sdl)!;
            if (Array.IndexOf(_sdlOnly, name) < 0)
            {
                Assert.Equal(name, Enum.GetName(translated));
            }
        }
    }

    [Theory]
    [InlineData(1u, (ushort)1)] // in range without a member: kept as a value
    [InlineData(511u, (ushort)511)]
    [InlineData(65535u, (ushort)65535)] // the largest value that fits
    [InlineData(65536u, (ushort)0)] // the first that doesn't: Unknown, not 0 by wrapping
    [InlineData(0x10004u, (ushort)0)] // would wrap to A (4)
    [InlineData(uint.MaxValue, (ushort)0)]
    public void Translate_ScancodeOutsideRange_IsUnknown(uint sdl, ushort expected)
    {
        Assert.Equal((Scancode)expected, TranslateKey((SdlScancode)sdl));
    }

    private static Scancode TranslateKey(SdlScancode scancode)
    {
        SdlEvent sdlEvent = new() { key = new SdlKeyboardEvent { type = SdlEventType.KeyDown, windowID = 1, scancode = scancode, down = 1 } };
        PlatformEventBuffer buffer = new(64, 2048);
        NoScales scales = default;

        Assert.True(SdlEventTranslator.TryAdd(sdlEvent, buffer, 0, ref scales));
        return buffer.AsBatch().Events[0].Key.Scancode;
    }

    // Key events never read a scale.
    private struct NoScales : SdlEventTranslator.IScaleSource
    {
        public readonly bool TryGetWindowDisplayScale(WindowId window, out float scale) => throw new InvalidOperationException();

        public readonly bool TryGetDisplayContentScale(DisplayId display, out float scale) => throw new InvalidOperationException();
    }
}
