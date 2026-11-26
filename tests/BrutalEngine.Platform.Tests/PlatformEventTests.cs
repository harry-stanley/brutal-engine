using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Xunit;

namespace BrutalEngine.Platform.Tests;

public sealed class PlatformEventTests
{
    private const int EventSize = 40;
    private const long SomeTimestamp = 42;

    private static readonly WindowId _someWindow = new(7);

    // Written from docs/design/platform.md, "The event struct" (the kind-to-factory table and the window rules), not
    // from the code under test: each kind, the one factory that accepts it ("" for none), and what its header's
    // Window must be. 'R': a real (non-default) window, the Window* kinds. 'D': default, QuitRequested and the
    // Display* kinds. 'A': either, the key, text and mouse kinds.
    private static readonly (PlatformEventKind Kind, string Factory, char Window)[] _kinds =
    [
        (PlatformEventKind.None, "", 'A'),
        (PlatformEventKind.QuitRequested, "Create", 'D'),
        (PlatformEventKind.WindowShown, "Create", 'R'),
        (PlatformEventKind.WindowHidden, "Create", 'R'),
        (PlatformEventKind.WindowMoved, "Create", 'R'),
        (PlatformEventKind.WindowResized, "CreateSize", 'R'),
        (PlatformEventKind.WindowPixelSizeChanged, "CreateSize", 'R'),
        (PlatformEventKind.WindowDisplayScaleChanged, "CreateScale", 'R'),
        (PlatformEventKind.WindowDisplayChanged, "CreateDisplay", 'R'),
        (PlatformEventKind.WindowMinimized, "Create", 'R'),
        (PlatformEventKind.WindowMaximized, "Create", 'R'),
        (PlatformEventKind.WindowRestored, "Create", 'R'),
        (PlatformEventKind.WindowFocusGained, "Create", 'R'),
        (PlatformEventKind.WindowFocusLost, "Create", 'R'),
        (PlatformEventKind.WindowMouseEntered, "Create", 'R'),
        (PlatformEventKind.WindowMouseLeft, "Create", 'R'),
        (PlatformEventKind.WindowOccluded, "Create", 'R'),
        (PlatformEventKind.WindowExposed, "Create", 'R'),
        (PlatformEventKind.WindowCloseRequested, "Create", 'R'),
        (PlatformEventKind.KeyDown, "CreateKey", 'A'),
        (PlatformEventKind.KeyUp, "CreateKey", 'A'),
        (PlatformEventKind.TextInput, "CreateText", 'A'),
        (PlatformEventKind.MouseMotion, "CreateMouseMotion", 'A'),
        (PlatformEventKind.MouseButtonDown, "CreateMouseButton", 'A'),
        (PlatformEventKind.MouseButtonUp, "CreateMouseButton", 'A'),
        (PlatformEventKind.MouseWheel, "CreateMouseWheel", 'A'),
        (PlatformEventKind.DisplayAdded, "CreateDisplay", 'D'),
        (PlatformEventKind.DisplayRemoved, "CreateDisplay", 'D'),
        (PlatformEventKind.DisplayContentScaleChanged, "CreateDisplayScale", 'D'),
        (PlatformEventKind.DisplayCurrentModeChanged, "CreateDisplay", 'D'),
    ];

    // Values without a member: the first above the last kind, and the largest (also what (PlatformEventKind)(-1) is).
    private static readonly PlatformEventKind[] _undefinedKinds =
        [(PlatformEventKind)30, (PlatformEventKind)ushort.MaxValue];

    private static readonly string[] _factories =
    [
        "Create", "CreateKey", "CreateText", "CreateMouseMotion", "CreateMouseButton", "CreateMouseWheel",
        "CreateSize", "CreateDisplay", "CreateScale", "CreateDisplayScale",
    ];

    // The payload views, each with the factory whose events carry it ("CreateDisplayScale" carries two) and the
    // offset docs/design/platform.md gives it. The extents come from the payload sizes PayloadLayoutTests pins.
    private static readonly (string View, string Factory, int Offset)[] _views =
    [
        ("Key", "CreateKey", 16), ("Text", "CreateText", 16), ("MouseMotion", "CreateMouseMotion", 16),
        ("MouseButton", "CreateMouseButton", 16), ("MouseWheel", "CreateMouseWheel", 16), ("Size", "CreateSize", 16),
        ("Display", "CreateDisplay", 16), ("Display", "CreateDisplayScale", 16), ("Scale", "CreateScale", 20),
        ("Scale", "CreateDisplayScale", 20),
    ];

    private static readonly string[] _viewNames =
        ["Key", "Text", "MouseMotion", "MouseButton", "MouseWheel", "Size", "Display", "Scale"];

    private static readonly KeyEventData _sampleKey = new(Scancode.A, new Keycode(97), KeyModifiers.LeftShift, true);
    private static readonly TextEventData _sampleText = new(3, 5);
    private static readonly MouseMotionData _sampleMotion = new(1.5f, -2.5f, 3.25f, -4.75f, MouseButtons.X2);
    private static readonly MouseButtonData _sampleButton = new(Platform.MouseButton.X1, 2, 10.5f, 20.25f);
    private static readonly MouseWheelData _sampleWheel = new(0.5f, -1.5f, 30.5f, 40.25f);
    private static readonly WindowSize _sampleSize = new(800, 600);
    private static readonly DisplayId _sampleDisplay = new(0xDEADBEEF);
    private static readonly float _sampleScale = 1.75f;

    public static TheoryData<PlatformEventKind, string> KindsAndFactories()
    {
        TheoryData<PlatformEventKind, string> data = [];
        foreach (PlatformEventKind kind in AllKinds())
        {
            foreach (string factory in _factories)
            {
                data.Add(kind, factory);
            }
        }

        return data;
    }

    public static TheoryData<PlatformEventKind, string> KindsAndViews()
    {
        TheoryData<PlatformEventKind, string> data = [];
        foreach (PlatformEventKind kind in AllKinds())
        {
            foreach (string view in _viewNames)
            {
                data.Add(kind, view);
            }
        }

        return data;
    }

    public static TheoryData<PlatformEventKind> KindsWithWindowRule(char window)
    {
        TheoryData<PlatformEventKind> data = [];
        foreach ((PlatformEventKind kind, string factory, char rule) in _kinds)
        {
            if (factory.Length > 0 && rule == window)
            {
                data.Add(kind);
            }
        }

        return data;
    }

    public static TheoryData<string, string> Views()
    {
        TheoryData<string, string> data = [];
        foreach ((string view, string factory, _) in _views)
        {
            data.Add(view, factory);
        }

        return data;
    }

    [Fact]
    public void Size_Is40Bytes()
    {
        Assert.Equal(EventSize, Unsafe.SizeOf<PlatformEvent>());
        Assert.False(RuntimeHelpers.IsReferenceOrContainsReferences<PlatformEvent>());
    }

    [Fact]
    public void Header_IsAtOffsets0And4And8()
    {
        PlatformEventHeader header = new(PlatformEventKind.WindowShown, new WindowId(0x01020304), -2);

        byte[] bytes = BytesOf(PlatformEvent.Create(header));

        Assert.Equal((ushort)PlatformEventKind.WindowShown, Read<ushort>(bytes, 0));
        Assert.Equal(0, Read<ushort>(bytes, 2));
        Assert.Equal(0x01020304u, Read<uint>(bytes, 4));
        Assert.Equal(-2L, Read<long>(bytes, 8));
        Assert.Equal(new byte[EventSize - 16], bytes[16..]);
    }

    [Theory]
    [MemberData(nameof(Views))]
    public void View_ReadsDocumentedOffset(string view, string factory)
    {
        PlatformEventKind kind = Array.Find(_kinds, k => k.Factory == factory).Kind;
        PlatformEventHeader header = new(kind, ValidWindow(kind), SomeTimestamp);
        byte[] expected = new byte[EventSize];
        Unsafe.WriteUnaligned(ref expected[0], (ushort)kind);
        Unsafe.WriteUnaligned(ref expected[4], header.Window);
        Unsafe.WriteUnaligned(ref expected[8], SomeTimestamp);
        foreach ((string otherView, string otherFactory, int otherOffset) in _views)
        {
            if (otherFactory == factory)
            {
                WritePayload(expected, otherView, otherOffset);
            }
        }

        // The factory writes the payload at the documented offset and zeroes the rest; the view reads it back from
        // there, including from an event that only ever existed as bytes.
        PlatformEvent created = Invoke(factory, header);
        Assert.Equal(expected, BytesOf(created));
        Assert.Equal(Expected(view), ReadView(created, view));
        Assert.Equal(Expected(view), ReadView(Unsafe.ReadUnaligned<PlatformEvent>(ref expected[0]), view));
    }

    [Theory]
    [MemberData(nameof(KindsAndFactories))]
    public void EveryKind_AcceptedByExactlyOneFactory(PlatformEventKind kind, string factory)
    {
        PlatformEventHeader header = new(kind, ValidWindow(kind), SomeTimestamp);
        string owner = FactoryOf(kind);

        if (owner == factory)
        {
            Assert.Equal(header, Invoke(factory, header).Header);
        }
        else
        {
            ArgumentException ex = Assert.Throws<ArgumentException>(() => Invoke(factory, header));
            Assert.Equal("header", ex.ParamName);
            Assert.Contains(owner.Length > 0 ? $"created by {owner}," : "has no factory", ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void KindTable_CoversEveryKind_EachWithOneFactory()
    {
        Assert.Equal(Enum.GetValues<PlatformEventKind>(), Array.ConvertAll(_kinds, k => k.Kind));
        Assert.Equal(29, Array.FindAll(_kinds, k => Array.IndexOf(_factories, k.Factory) >= 0).Length);
        Assert.Equal("", FactoryOf(PlatformEventKind.None));
    }

    [Theory]
    [MemberData(nameof(KindsWithWindowRule), 'R')]
    public void WindowKind_WithDefaultWindow_Throws(PlatformEventKind kind)
    {
        PlatformEventHeader header = new(kind, default, SomeTimestamp);

        ArgumentException ex = Assert.Throws<ArgumentException>(() => Invoke(FactoryOf(kind), header));
        Assert.Equal("header", ex.ParamName);
        Assert.Contains("needs a window", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(KindsWithWindowRule), 'D')]
    public void DisplayKind_WithWindow_Throws(PlatformEventKind kind)
    {
        foreach (WindowId window in (WindowId[])[new(1), new(uint.MaxValue)])
        {
            PlatformEventHeader header = new(kind, window, SomeTimestamp);

            ArgumentException ex = Assert.Throws<ArgumentException>(() => Invoke(FactoryOf(kind), header));
            Assert.Equal("header", ex.ParamName);
            Assert.Contains("platform-wide", ex.Message, StringComparison.Ordinal);
        }
    }

    [Theory]
    [MemberData(nameof(KindsWithWindowRule), 'A')]
    public void KeyKind_WithDefaultWindow_Accepted(PlatformEventKind kind)
    {
        foreach (WindowId window in (WindowId[])[default, _someWindow, new(uint.MaxValue)])
        {
            PlatformEventHeader header = new(kind, window, SomeTimestamp);

            Assert.Equal(header, Invoke(FactoryOf(kind), header).Header);
        }
    }

    [Theory]
    [MemberData(nameof(KindsAndViews))]
    public void WrongView_Throws(PlatformEventKind kind, string view)
    {
        PlatformEvent e = Make(kind);
        bool carried = Array.Exists(_views, v => v.View == view && v.Factory == FactoryOf(kind));

        if (carried)
        {
            Assert.Equal(Expected(view), ReadView(e, view));
        }
        else
        {
            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => ReadView(e, view));
            Assert.Contains($"doesn't carry {view}", ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void DisplayContentScaleChanged_ReadsDisplayAndScale()
    {
        PlatformEventHeader header = new(PlatformEventKind.DisplayContentScaleChanged, default, SomeTimestamp);

        var e = PlatformEvent.CreateDisplayScale(header, new DisplayId(3), 2.5f);
        byte[] bytes = BytesOf(e);

        Assert.Equal(new DisplayId(3), e.Display);
        Assert.Equal(2.5f, e.Scale);
        Assert.Equal(3u, Read<uint>(bytes, 16));
        Assert.Equal(2.5f, Read<float>(bytes, 20));
    }

    [Theory]
    [InlineData(0u, float.NaN)]
    [InlineData(0u, float.NegativeInfinity)]
    [InlineData(uint.MaxValue, -1f)]
    [InlineData(uint.MaxValue, 0f)]
    public void ScaleAndDisplayFactories_StoreValuesAsGiven(uint display, float scale)
    {
        PlatformEventHeader windowHeader = new(PlatformEventKind.WindowDisplayScaleChanged, _someWindow, SomeTimestamp);
        PlatformEventHeader displayHeader = new(PlatformEventKind.DisplayAdded, default, SomeTimestamp);
        PlatformEventHeader bothHeader = displayHeader with { Kind = PlatformEventKind.DisplayContentScaleChanged };

        var both = PlatformEvent.CreateDisplayScale(bothHeader, new DisplayId(display), scale);

        Assert.Equal(scale, PlatformEvent.CreateScale(windowHeader, scale).Scale);
        Assert.Equal(new DisplayId(display), PlatformEvent.CreateDisplay(displayHeader, new DisplayId(display)).Display);
        Assert.Equal((new DisplayId(display), scale), (both.Display, both.Scale));
    }

    [Fact]
    public void CreateAndRead_DoNotAllocate()
    {
        _ = CreateAndReadAll();
        long before = GC.GetAllocatedBytesForCurrentThread();

        float checksum = CreateAndReadAll();

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
        Assert.NotEqual(0f, checksum);
    }

    private static PlatformEventKind[] AllKinds() => [.. Array.ConvertAll(_kinds, k => k.Kind), .. _undefinedKinds];

    private static string FactoryOf(PlatformEventKind kind) => Array.Find(_kinds, k => k.Kind == kind).Factory ?? "";

    private static WindowId ValidWindow(PlatformEventKind kind) =>
        Array.Find(_kinds, k => k.Kind == kind).Window == 'R' ? _someWindow : default;

    private static PlatformEvent Invoke(string factory, PlatformEventHeader header) => factory switch
    {
        "Create" => PlatformEvent.Create(header),
        "CreateKey" => PlatformEvent.CreateKey(header, _sampleKey),
        "CreateText" => PlatformEvent.CreateText(header, _sampleText),
        "CreateMouseMotion" => PlatformEvent.CreateMouseMotion(header, _sampleMotion),
        "CreateMouseButton" => PlatformEvent.CreateMouseButton(header, _sampleButton),
        "CreateMouseWheel" => PlatformEvent.CreateMouseWheel(header, _sampleWheel),
        "CreateSize" => PlatformEvent.CreateSize(header, _sampleSize),
        "CreateDisplay" => PlatformEvent.CreateDisplay(header, _sampleDisplay),
        "CreateScale" => PlatformEvent.CreateScale(header, _sampleScale),
        "CreateDisplayScale" => PlatformEvent.CreateDisplayScale(header, _sampleDisplay, _sampleScale),
        _ => throw new ArgumentOutOfRangeException(nameof(factory), factory, "Not a factory."),
    };

    // An event of any kind. None and values without a member, which no factory creates, are written as raw bytes, as
    // a zeroed or corrupted buffer slot would hold them (None is default(PlatformEvent)).
    private static PlatformEvent Make(PlatformEventKind kind)
    {
        string factory = FactoryOf(kind);
        if (factory.Length > 0)
        {
            return Invoke(factory, new PlatformEventHeader(kind, ValidWindow(kind), SomeTimestamp));
        }

        byte[] bytes = new byte[EventSize];
        Unsafe.WriteUnaligned(ref bytes[0], (ushort)kind);
        return Unsafe.ReadUnaligned<PlatformEvent>(ref bytes[0]);
    }

    private static object Expected(string view) => view switch
    {
        "Key" => _sampleKey,
        "Text" => _sampleText,
        "MouseMotion" => _sampleMotion,
        "MouseButton" => _sampleButton,
        "MouseWheel" => _sampleWheel,
        "Size" => _sampleSize,
        "Display" => _sampleDisplay,
        _ => _sampleScale,
    };

    private static object ReadView(PlatformEvent e, string view) => view switch
    {
        "Key" => e.Key,
        "Text" => e.Text,
        "MouseMotion" => e.MouseMotion,
        "MouseButton" => e.MouseButton,
        "MouseWheel" => e.MouseWheel,
        "Size" => e.Size,
        "Display" => e.Display,
        _ => e.Scale,
    };

    private static void WritePayload(byte[] bytes, string view, int offset)
    {
        byte[] payload = view switch
        {
            "Key" => BytesOf(_sampleKey),
            "Text" => BytesOf(_sampleText),
            "MouseMotion" => BytesOf(_sampleMotion),
            "MouseButton" => BytesOf(_sampleButton),
            "MouseWheel" => BytesOf(_sampleWheel),
            "Size" => BytesOf(_sampleSize),
            "Display" => BytesOf(_sampleDisplay),
            _ => BytesOf(_sampleScale),
        };
        payload.CopyTo(bytes, offset);
    }

    private static byte[] BytesOf<T>(T value)
        where T : unmanaged => MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in value)).ToArray();

    private static T Read<T>(byte[] bytes, int offset)
        where T : unmanaged => Unsafe.ReadUnaligned<T>(ref bytes[offset]);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static float CreateAndReadAll()
    {
        PlatformEventHeader window = new(PlatformEventKind.WindowResized, _someWindow, 1);
        PlatformEventHeader display = new(PlatformEventKind.DisplayContentScaleChanged, default, 2);
        float sum = 0;
        sum += PlatformEvent.Create(new PlatformEventHeader(PlatformEventKind.QuitRequested, default, 3)).Timestamp;
        sum += (int)PlatformEvent.CreateKey(window with { Kind = PlatformEventKind.KeyDown }, _sampleKey).Key.Scancode;
        sum += PlatformEvent.CreateText(window with { Kind = PlatformEventKind.TextInput }, _sampleText).Text.Length;
        sum += PlatformEvent.CreateMouseMotion(window with { Kind = PlatformEventKind.MouseMotion }, _sampleMotion)
            .MouseMotion.X;
        sum += PlatformEvent.CreateMouseButton(window with { Kind = PlatformEventKind.MouseButtonUp }, _sampleButton)
            .MouseButton.Y;
        sum += PlatformEvent.CreateMouseWheel(window with { Kind = PlatformEventKind.MouseWheel }, _sampleWheel)
            .MouseWheel.Y;
        sum += PlatformEvent.CreateSize(window, _sampleSize).Size.Width;
        sum += PlatformEvent.CreateDisplay(display with { Kind = PlatformEventKind.DisplayAdded }, _sampleDisplay)
            .Display.Value;
        sum += PlatformEvent.CreateScale(window with { Kind = PlatformEventKind.WindowDisplayScaleChanged }, _sampleScale)
            .Scale;
        var both = PlatformEvent.CreateDisplayScale(display, _sampleDisplay, _sampleScale);
        sum += both.Display.Value + both.Scale + both.Header.Timestamp + both.Window.Value + (ushort)both.Kind;
        return sum;
    }
}
