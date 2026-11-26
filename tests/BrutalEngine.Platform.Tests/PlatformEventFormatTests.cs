using System;
using System.Runtime.CompilerServices;
using Xunit;

namespace BrutalEngine.Platform.Tests;

public sealed class PlatformEventFormatTests
{
    private const int EventSize = 40;
    private const long SampleTimestamp = -120;

    // Written from docs/design/platform.md, "The event struct", not from the code under test: every kind, in the
    // enum's order, with the payload its one factory writes ("" for None, which no factory accepts; "None" for the
    // factory without a payload) and what its header's Window must be. 'R': a real (non-default) window, the
    // Window* kinds. 'D': default, QuitRequested and the Display* kinds. 'A': either, the key, text and mouse kinds.
    // '-': no rule, because no factory accepts the kind.
    private static readonly (PlatformEventKind Kind, string Payload, char Window)[] _kinds =
    [
        (PlatformEventKind.None, "", '-'),
        (PlatformEventKind.QuitRequested, "None", 'D'),
        (PlatformEventKind.WindowShown, "None", 'R'),
        (PlatformEventKind.WindowHidden, "None", 'R'),
        (PlatformEventKind.WindowMoved, "None", 'R'),
        (PlatformEventKind.WindowResized, "Size", 'R'),
        (PlatformEventKind.WindowPixelSizeChanged, "Size", 'R'),
        (PlatformEventKind.WindowDisplayScaleChanged, "Scale", 'R'),
        (PlatformEventKind.WindowDisplayChanged, "Display", 'R'),
        (PlatformEventKind.WindowMinimized, "None", 'R'),
        (PlatformEventKind.WindowMaximized, "None", 'R'),
        (PlatformEventKind.WindowRestored, "None", 'R'),
        (PlatformEventKind.WindowFocusGained, "None", 'R'),
        (PlatformEventKind.WindowFocusLost, "None", 'R'),
        (PlatformEventKind.WindowMouseEntered, "None", 'R'),
        (PlatformEventKind.WindowMouseLeft, "None", 'R'),
        (PlatformEventKind.WindowOccluded, "None", 'R'),
        (PlatformEventKind.WindowExposed, "None", 'R'),
        (PlatformEventKind.WindowCloseRequested, "None", 'R'),
        (PlatformEventKind.KeyDown, "Key", 'A'),
        (PlatformEventKind.KeyUp, "Key", 'A'),
        (PlatformEventKind.TextInput, "Text", 'A'),
        (PlatformEventKind.MouseMotion, "MouseMotion", 'A'),
        (PlatformEventKind.MouseButtonDown, "MouseButton", 'A'),
        (PlatformEventKind.MouseButtonUp, "MouseButton", 'A'),
        (PlatformEventKind.MouseWheel, "MouseWheel", 'A'),
        (PlatformEventKind.DisplayAdded, "Display", 'D'),
        (PlatformEventKind.DisplayRemoved, "Display", 'D'),
        (PlatformEventKind.DisplayContentScaleChanged, "DisplayScale", 'D'),
        (PlatformEventKind.DisplayCurrentModeChanged, "Display", 'D'),
    ];

    private static readonly KeyEventData _sampleKey = new(Scancode.A, new Keycode(97), KeyModifiers.LeftShift, true);
    private static readonly TextEventData _sampleText = new(-3, 5);
    private static readonly MouseMotionData _sampleMotion = new(1.5f, -2.5f, 3.25f, -4.75f, MouseButtons.X2);
    private static readonly MouseButtonData _sampleButton = new(MouseButton.X1, 2, 10.5f, -20.25f);
    private static readonly MouseWheelData _sampleWheel = new(0.5f, -1.5f, 30.5f, 40.25f);
    private static readonly WindowSize _sampleSize = new(-800, 600);
    private static readonly DisplayId _sampleDisplay = new(0xDEADBEEF);
    private const float SampleScale = -1.75f;

    public static TheoryData<PlatformEventKind> KindsWithAFactory()
    {
        TheoryData<PlatformEventKind> data = [];
        foreach ((PlatformEventKind kind, string payload, _) in _kinds)
        {
            if (payload.Length > 0)
            {
                data.Add(kind);
            }
        }

        return data;
    }

    [Fact]
    public void KindTable_CoversEveryKind()
    {
        // A kind appended to the enum fails here until it gets a row: a payload and a window rule.
        Assert.Equal(Enum.GetValues<PlatformEventKind>(), Array.ConvertAll(_kinds, k => k.Kind));
        Assert.All(_kinds, k => Assert.Contains(k.Window, k.Payload.Length > 0 ? "RDA" : "-"));
    }

    [Theory]
    [MemberData(nameof(KindsWithAFactory))]
    public void WindowRule_EveryKind_MatchesTable(PlatformEventKind kind)
    {
        char rule = Row(kind).Window;
        foreach (WindowId window in (WindowId[])[default, new(1), new(uint.MaxValue)])
        {
            PlatformEventHeader header = new(kind, window, SampleTimestamp);
            bool allowed = rule == 'A' || (rule == 'R') == (window != default);

            if (allowed)
            {
                Assert.Equal(header, Make(kind, window).Header);
            }
            else
            {
                ArgumentException ex = Assert.Throws<ArgumentException>(() => Make(kind, window));
                Assert.Equal("header", ex.ParamName);
                Assert.Contains(rule == 'R' ? "needs a window" : "platform-wide", ex.Message, StringComparison.Ordinal);
            }
        }
    }

    private static (PlatformEventKind Kind, string Payload, char Window) Row(PlatformEventKind kind) =>
        Array.Find(_kinds, k => k.Kind == kind);

    // An event of any kind with the sample payloads. Kinds without a factory (None and values without a member) are
    // written as raw bytes, as a corrupted buffer slot would hold them, with every payload byte set.
    private static PlatformEvent Make(PlatformEventKind kind, WindowId window)
    {
        PlatformEventHeader header = new(kind, window, SampleTimestamp);
        string payload = Array.Exists(_kinds, k => k.Kind == kind) ? Row(kind).Payload : "";
        return payload switch
        {
            "None" => PlatformEvent.Create(header),
            "Key" => PlatformEvent.CreateKey(header, _sampleKey),
            "Text" => PlatformEvent.CreateText(header, _sampleText),
            "MouseMotion" => PlatformEvent.CreateMouseMotion(header, _sampleMotion),
            "MouseButton" => PlatformEvent.CreateMouseButton(header, _sampleButton),
            "MouseWheel" => PlatformEvent.CreateMouseWheel(header, _sampleWheel),
            "Size" => PlatformEvent.CreateSize(header, _sampleSize),
            "Display" => PlatformEvent.CreateDisplay(header, _sampleDisplay),
            "Scale" => PlatformEvent.CreateScale(header, SampleScale),
            "DisplayScale" => PlatformEvent.CreateDisplayScale(header, _sampleDisplay, SampleScale),
            _ => FromBytes(header),
        };
    }

    private static PlatformEvent FromBytes(PlatformEventHeader header)
    {
        byte[] raw = new byte[EventSize];
        raw.AsSpan(16).Fill(0xFF);
        Unsafe.WriteUnaligned(ref raw[0], (ushort)header.Kind);
        Unsafe.WriteUnaligned(ref raw[4], header.Window);
        Unsafe.WriteUnaligned(ref raw[8], header.Timestamp);
        return Unsafe.ReadUnaligned<PlatformEvent>(ref raw[0]);
    }
}
