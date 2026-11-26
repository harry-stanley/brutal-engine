using System;
using System.Linq;
using Xunit;

namespace BrutalEngine.Platform.Tests;

public sealed class PlatformEventKindTests
{
    // Written from docs/design/platform.md, "The event struct": None = 0, then the M1 kinds in the order the note lists
    // them. Kinds are appended, never renumbered, so these values are fixed.
    private static readonly (string Name, ushort Value)[] _expected =
    [
        ("None", 0), ("QuitRequested", 1),
        ("WindowShown", 2), ("WindowHidden", 3), ("WindowMoved", 4), ("WindowResized", 5),
        ("WindowPixelSizeChanged", 6), ("WindowDisplayScaleChanged", 7), ("WindowDisplayChanged", 8),
        ("WindowMinimized", 9), ("WindowMaximized", 10), ("WindowRestored", 11), ("WindowFocusGained", 12),
        ("WindowFocusLost", 13), ("WindowMouseEntered", 14), ("WindowMouseLeft", 15), ("WindowOccluded", 16),
        ("WindowExposed", 17), ("WindowCloseRequested", 18),
        ("KeyDown", 19), ("KeyUp", 20), ("TextInput", 21),
        ("MouseMotion", 22), ("MouseButtonDown", 23), ("MouseButtonUp", 24), ("MouseWheel", 25),
        ("DisplayAdded", 26), ("DisplayRemoved", 27), ("DisplayContentScaleChanged", 28),
        ("DisplayCurrentModeChanged", 29),
    ];

    [Fact]
    public void Values_MatchDesign()
    {
        (string, ushort)[] actual =
        [
            .. Enum.GetValues<PlatformEventKind>().Select(kind => (Enum.GetName(kind)!, (ushort)kind)),
        ];

        Assert.Equal(typeof(ushort), Enum.GetUnderlyingType(typeof(PlatformEventKind)));
        Assert.Equal(30, _expected.Length);
        Assert.Equal(_expected, actual);
    }
}
