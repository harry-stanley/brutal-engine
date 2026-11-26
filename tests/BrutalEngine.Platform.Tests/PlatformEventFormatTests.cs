using System;
using System.Globalization;
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

    // Values without a member, as ToString prints them: the first above the last kind, and the largest (also what
    // (PlatformEventKind)(-1) is).
    private static readonly (PlatformEventKind Kind, string Text)[] _undefinedKinds =
        [((PlatformEventKind)30, "30"), ((PlatformEventKind)ushort.MaxValue, "65535")];

    private static readonly WindowId _sampleWindow = new(7);

    // Negative numbers and fractions, so a culture's negative sign or decimal separator would show.
    private static readonly KeyEventData _sampleKey = new(Scancode.A, new Keycode(97), KeyModifiers.LeftShift, true);
    private static readonly TextEventData _sampleText = new(-3, 5);
    private static readonly MouseMotionData _sampleMotion = new(1.5f, -2.5f, 3.25f, -4.75f, MouseButtons.X2);
    private static readonly MouseButtonData _sampleButton = new(MouseButton.X1, 2, 10.5f, -20.25f);
    private static readonly MouseWheelData _sampleWheel = new(0.5f, -1.5f, 30.5f, 40.25f);
    private static readonly WindowSize _sampleSize = new(-800, 600);
    private static readonly DisplayId _sampleDisplay = new(0xDEADBEEF);
    private const float SampleScale = -1.75f;

    // What each payload of the samples above prints after the header, written by hand in the payload records' usual
    // form with the invariant culture.
    private static readonly (string Payload, string Text)[] _samplePayloadTexts =
    [
        ("", ""),
        ("None", ""),
        ("Key", ", Key = KeyEventData { Scancode = A, Keycode = Keycode { Value = 97 }, Modifiers = LeftShift, IsRepeat = True }"),
        ("Text", ", Text = TextEventData { Offset = -3, Length = 5 }"),
        ("MouseMotion", ", MouseMotion = MouseMotionData { X = 1.5, Y = -2.5, DeltaX = 3.25, DeltaY = -4.75, Buttons = X2 }"),
        ("MouseButton", ", MouseButton = MouseButtonData { Button = X1, Clicks = 2, X = 10.5, Y = -20.25 }"),
        ("MouseWheel", ", MouseWheel = MouseWheelData { X = 0.5, Y = -1.5, MouseX = 30.5, MouseY = 40.25 }"),
        ("Size", ", Size = WindowSize { Width = -800, Height = 600 }"),
        ("Display", ", Display = DisplayId { Value = 3735928559 }"),
        ("Scale", ", Scale = -1.75"),
        ("DisplayScale", ", Display = DisplayId { Value = 3735928559 }, Scale = -1.75"),
    ];

    public static TheoryData<PlatformEventKind> EveryKindAndUndefined()
    {
        TheoryData<PlatformEventKind> data = [];
        foreach ((PlatformEventKind kind, _, _) in _kinds)
        {
            data.Add(kind);
        }

        foreach ((PlatformEventKind kind, _) in _undefinedKinds)
        {
            data.Add(kind);
        }

        return data;
    }

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

    [Theory]
    [MemberData(nameof(EveryKindAndUndefined))]
    public void ToString_EveryKind_DoesNotThrow(PlatformEventKind kind)
    {
        PlatformEvent e = Make(kind, SampleWindowFor(kind));

        Assert.Equal(Expected(kind), e.ToString());
    }

    [Fact]
    public void ToString_Default_PrintsHeaderOnly()
    {
        Assert.Equal("PlatformEvent { Kind = None, Window = 0, Timestamp = 0 }", default(PlatformEvent).ToString());
    }

    [Fact]
    public void ToString_UnderAnotherCulture_IsInvariant()
    {
        // A culture whose every number symbol differs from the invariant one, so any culture-sensitive formatting
        // shows. Cloned from the invariant culture, so the test doesn't depend on the OS's culture data.
        var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        culture.NumberFormat.NegativeSign = "~";
        culture.NumberFormat.NumberDecimalSeparator = ",";
        culture.NumberFormat.NumberGroupSeparator = "_";
        culture.NumberFormat.NaNSymbol = "nan!";
        culture.NumberFormat.PositiveInfinitySymbol = "inf!";
        culture.NumberFormat.NegativeInfinitySymbol = "~inf!";
        PlatformEventHeader textHeader = new(PlatformEventKind.TextInput, default, long.MinValue);
        PlatformEventHeader sizeHeader = new(PlatformEventKind.WindowPixelSizeChanged, _sampleWindow, -1);
        PlatformEventHeader scaleHeader = new(PlatformEventKind.WindowDisplayScaleChanged, _sampleWindow, 0);
        PlatformEventHeader bothHeader = new(PlatformEventKind.DisplayContentScaleChanged, default, 0);
        (PlatformEvent Event, string Expected)[] edges =
        [
            (PlatformEvent.CreateText(textHeader, new TextEventData(int.MinValue, int.MaxValue)),
                "PlatformEvent { Kind = TextInput, Window = 0, Timestamp = -9223372036854775808, "
                + "Text = TextEventData { Offset = -2147483648, Length = 2147483647 } }"),
            (PlatformEvent.CreateSize(sizeHeader, new WindowSize(-1, int.MinValue)),
                "PlatformEvent { Kind = WindowPixelSizeChanged, Window = 7, Timestamp = -1, "
                + "Size = WindowSize { Width = -1, Height = -2147483648 } }"),
            (PlatformEvent.CreateScale(scaleHeader, float.NaN),
                "PlatformEvent { Kind = WindowDisplayScaleChanged, Window = 7, Timestamp = 0, Scale = NaN }"),
            (PlatformEvent.CreateScale(scaleHeader, float.PositiveInfinity),
                "PlatformEvent { Kind = WindowDisplayScaleChanged, Window = 7, Timestamp = 0, Scale = Infinity }"),
            (PlatformEvent.CreateDisplayScale(bothHeader, default, float.NegativeInfinity),
                "PlatformEvent { Kind = DisplayContentScaleChanged, Window = 0, Timestamp = 0, "
                + "Display = DisplayId { Value = 0 }, Scale = -Infinity }"),
            (PlatformEvent.CreateDisplayScale(bothHeader, new DisplayId(uint.MaxValue), -0.5f),
                "PlatformEvent { Kind = DisplayContentScaleChanged, Window = 0, Timestamp = 0, "
                + "Display = DisplayId { Value = 4294967295 }, Scale = -0.5 }"),
        ];

        CultureInfo previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = culture;
        try
        {
            Assert.Equal("~1,5", (-1.5).ToString(CultureInfo.CurrentCulture));
            foreach ((PlatformEventKind kind, _, _) in _kinds)
            {
                Assert.Equal(Expected(kind), Make(kind, SampleWindowFor(kind)).ToString());
            }

            foreach ((PlatformEvent e, string expected) in edges)
            {
                Assert.Equal(expected, e.ToString());
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    private static (PlatformEventKind Kind, string Payload, char Window) Row(PlatformEventKind kind) =>
        Array.Find(_kinds, k => k.Kind == kind);

    private static WindowId SampleWindowFor(PlatformEventKind kind) =>
        Row(kind).Window == 'D' ? default : _sampleWindow;

    // An event of any kind with the sample payloads. Kinds without a factory (None and values without a member) are
    // written as raw bytes, as a corrupted buffer slot would hold them, with every payload byte set, so a ToString
    // that read a payload for them would print it.
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

    private static string Expected(PlatformEventKind kind)
    {
        int undefined = Array.FindIndex(_undefinedKinds, k => k.Kind == kind);
        string name = undefined >= 0 ? _undefinedKinds[undefined].Text : kind.ToString();
        string payload = undefined >= 0 ? "" : Row(kind).Payload;
        uint window = SampleWindowFor(kind).Value;
        string payloadText = Array.Find(_samplePayloadTexts, p => p.Payload == payload).Text;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"PlatformEvent {{ Kind = {name}, Window = {window}, Timestamp = -120{payloadText} }}");
    }
}
