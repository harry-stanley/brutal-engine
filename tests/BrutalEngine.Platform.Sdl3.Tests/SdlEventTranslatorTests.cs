using System;
using Brutal.SdlApi;
using Xunit;

namespace BrutalEngine.Platform.Sdl3.Tests;

// In the SDL collection for the production scale source's rows, which start SDL video; the rest are pure.
[Collection(SdlNativeTests.Name)]
public sealed class SdlEventTranslatorTests
{
    // Literal inputs every row shares: distinct values, so a payload read from the wrong field shows.
    private const uint WindowIdValue = 7;
    private const uint DisplayIdValue = 5;
    private const int Data1 = 640;
    private const int Data2 = 480;
    private const long Timestamp = 123_456_789;
    private const float WindowScale = 1.5f;
    private const float DisplayScale = 2.25f;

    // One row per SDL quit, window and display type the engine maps, with the kind written from the design's table
    // (docs/design/platform.md, "The event struct"), not from the code under test.
    [Theory]
    [InlineData(SdlEventType.Quit, PlatformEventKind.QuitRequested)]
    [InlineData(SdlEventType.WindowShown, PlatformEventKind.WindowShown)]
    [InlineData(SdlEventType.WindowHidden, PlatformEventKind.WindowHidden)]
    [InlineData(SdlEventType.WindowExposed, PlatformEventKind.WindowExposed)]
    [InlineData(SdlEventType.WindowMoved, PlatformEventKind.WindowMoved)]
    [InlineData(SdlEventType.WindowResized, PlatformEventKind.WindowResized)]
    [InlineData(SdlEventType.WindowPixelSizeChanged, PlatformEventKind.WindowPixelSizeChanged)]
    [InlineData(SdlEventType.WindowMinimized, PlatformEventKind.WindowMinimized)]
    [InlineData(SdlEventType.WindowMaximized, PlatformEventKind.WindowMaximized)]
    [InlineData(SdlEventType.WindowRestored, PlatformEventKind.WindowRestored)]
    [InlineData(SdlEventType.WindowMouseEnter, PlatformEventKind.WindowMouseEntered)]
    [InlineData(SdlEventType.WindowMouseLeave, PlatformEventKind.WindowMouseLeft)]
    [InlineData(SdlEventType.WindowFocusGained, PlatformEventKind.WindowFocusGained)]
    [InlineData(SdlEventType.WindowFocusLost, PlatformEventKind.WindowFocusLost)]
    [InlineData(SdlEventType.WindowCloseRequested, PlatformEventKind.WindowCloseRequested)]
    [InlineData(SdlEventType.WindowDisplayChanged, PlatformEventKind.WindowDisplayChanged)]
    [InlineData(SdlEventType.WindowDisplayScaleChanged, PlatformEventKind.WindowDisplayScaleChanged)]
    [InlineData(SdlEventType.WindowOccluded, PlatformEventKind.WindowOccluded)]
    [InlineData(SdlEventType.DisplayAdded, PlatformEventKind.DisplayAdded)]
    [InlineData(SdlEventType.DisplayRemoved, PlatformEventKind.DisplayRemoved)]
    [InlineData(SdlEventType.DisplayCurrentModeChanged, PlatformEventKind.DisplayCurrentModeChanged)]
    [InlineData(SdlEventType.DisplayContentScaleChanged, PlatformEventKind.DisplayContentScaleChanged)]
    public void Translate_Table(SdlEventType type, PlatformEventKind kind)
    {
        PlatformEventBuffer buffer = NewBuffer();
        FakeScales scales = new(WindowIdValue, WindowScale, DisplayIdValue, DisplayScale);

        bool added = SdlEventTranslator.TryAdd(SdlEventOf(type, WindowIdValue), buffer, Timestamp, ref scales);

        Assert.True(added);
        PlatformEvent actual = Assert.Single(buffer.AsBatch().Events.ToArray());
        Assert.Equal(Expected(kind), actual);
        Assert.Equal(Expected(kind).ToString(), actual.ToString()); // names the payload when a row fails
    }

    [Theory]
    [InlineData(SdlEventType.WindowDisplayScaleChanged, true, 0.75f)]
    [InlineData(SdlEventType.WindowDisplayScaleChanged, true, 3f)]
    [InlineData(SdlEventType.WindowDisplayScaleChanged, false, 0f)] // the window is already gone
    [InlineData(SdlEventType.DisplayContentScaleChanged, true, 1.25f)]
    [InlineData(SdlEventType.DisplayContentScaleChanged, true, 4f)]
    [InlineData(SdlEventType.DisplayContentScaleChanged, false, 0f)] // the display is already unplugged
    public void Scale_UsesScaleSource(SdlEventType type, bool known, float scale)
    {
        PlatformEventBuffer buffer = NewBuffer();

        // The fake knows only these ids, so a query for any other (the wrong field, a constant) fails.
        FakeScales scales = known
            ? new FakeScales(WindowIdValue, scale, DisplayIdValue, scale)
            : new FakeScales(WindowIdValue + 1, 1f, DisplayIdValue + 1, 1f);

        bool added = SdlEventTranslator.TryAdd(SdlEventOf(type, WindowIdValue), buffer, Timestamp, ref scales);

        Assert.Equal(known, added);
        Assert.Equal(1, scales.Queries);
        Assert.Equal(known ? 1 : 0, buffer.Count);
        if (known)
        {
            PlatformEvent actual = buffer.AsBatch().Events[0];
            Assert.Equal(scale, actual.Scale);
            Assert.Equal(type == SdlEventType.WindowDisplayScaleChanged ? new WindowId(WindowIdValue) : default, actual.Window);
        }
    }

    [Theory]
    [InlineData(SdlEventType.First, WindowIdValue)]
    [InlineData(SdlEventType.Terminating, WindowIdValue)]
    [InlineData(SdlEventType.LowMemory, WindowIdValue)]
    [InlineData(SdlEventType.LocaleChanged, WindowIdValue)]
    [InlineData(SdlEventType.SystemThemeChanged, WindowIdValue)]
    [InlineData(SdlEventType.DisplayOrientation, WindowIdValue)]
    [InlineData(SdlEventType.DisplayMoved, WindowIdValue)]
    [InlineData(SdlEventType.DisplayDesktopModeChanged, WindowIdValue)]
    [InlineData(SdlEventType.DisplayUsableBoundsChanged, WindowIdValue)]
    [InlineData(SdlEventType.WindowMetalViewResized, WindowIdValue)]
    [InlineData(SdlEventType.WindowHitTest, WindowIdValue)]
    [InlineData(SdlEventType.WindowIccprofChanged, WindowIdValue)]
    [InlineData(SdlEventType.WindowSafeAreaChanged, WindowIdValue)]
    [InlineData(SdlEventType.WindowEnterFullscreen, WindowIdValue)]
    [InlineData(SdlEventType.WindowLeaveFullscreen, WindowIdValue)]
    [InlineData(SdlEventType.WindowDestroyed, WindowIdValue)]
    [InlineData(SdlEventType.WindowHdrStateChanged, WindowIdValue)]
    [InlineData(SdlEventType.TextEditing, WindowIdValue)]
    [InlineData(SdlEventType.TextEditingCandidates, WindowIdValue)]
    [InlineData(SdlEventType.KeymapChanged, WindowIdValue)]
    [InlineData(SdlEventType.KeyboardAdded, WindowIdValue)]
    [InlineData(SdlEventType.ScreenKeyboardShown, WindowIdValue)]
    [InlineData(SdlEventType.MouseAdded, WindowIdValue)]
    [InlineData(SdlEventType.MouseRemoved, WindowIdValue)]
    [InlineData(SdlEventType.JoystickAxisMotion, WindowIdValue)]
    [InlineData(SdlEventType.JoystickAdded, WindowIdValue)]
    [InlineData(SdlEventType.GamepadAxisMotion, WindowIdValue)]
    [InlineData(SdlEventType.GamepadButtonDown, WindowIdValue)]
    [InlineData(SdlEventType.GamepadAdded, WindowIdValue)]
    [InlineData(SdlEventType.GamepadTouchpadDown, WindowIdValue)]
    [InlineData(SdlEventType.FingerDown, WindowIdValue)]
    [InlineData(SdlEventType.FingerMotion, WindowIdValue)]
    [InlineData(SdlEventType.PinchBegin, WindowIdValue)]
    [InlineData(SdlEventType.ClipboardUpdate, WindowIdValue)]
    [InlineData(SdlEventType.DropFile, WindowIdValue)]
    [InlineData(SdlEventType.DropText, WindowIdValue)]
    [InlineData(SdlEventType.DropBegin, WindowIdValue)]
    [InlineData(SdlEventType.AudioDeviceAdded, WindowIdValue)]
    [InlineData(SdlEventType.AudioDeviceRemoved, WindowIdValue)]
    [InlineData(SdlEventType.SensorUpdate, WindowIdValue)]
    [InlineData(SdlEventType.PenProximityIn, WindowIdValue)]
    [InlineData(SdlEventType.PenDown, WindowIdValue)]
    [InlineData(SdlEventType.PenMotion, WindowIdValue)]
    [InlineData(SdlEventType.CameraDeviceAdded, WindowIdValue)]
    [InlineData(SdlEventType.User, WindowIdValue)]
    [InlineData((SdlEventType)uint.MaxValue, WindowIdValue)] // a type SDL may add later

    // Every window kind needs a window, so a window event SDL reports without one (id 0) is ignored.
    [InlineData(SdlEventType.WindowShown, 0u)]
    [InlineData(SdlEventType.WindowHidden, 0u)]
    [InlineData(SdlEventType.WindowExposed, 0u)]
    [InlineData(SdlEventType.WindowMoved, 0u)]
    [InlineData(SdlEventType.WindowResized, 0u)]
    [InlineData(SdlEventType.WindowPixelSizeChanged, 0u)]
    [InlineData(SdlEventType.WindowMinimized, 0u)]
    [InlineData(SdlEventType.WindowMaximized, 0u)]
    [InlineData(SdlEventType.WindowRestored, 0u)]
    [InlineData(SdlEventType.WindowMouseEnter, 0u)]
    [InlineData(SdlEventType.WindowMouseLeave, 0u)]
    [InlineData(SdlEventType.WindowFocusGained, 0u)]
    [InlineData(SdlEventType.WindowFocusLost, 0u)]
    [InlineData(SdlEventType.WindowCloseRequested, 0u)]
    [InlineData(SdlEventType.WindowDisplayChanged, 0u)]
    [InlineData(SdlEventType.WindowDisplayScaleChanged, 0u)]
    [InlineData(SdlEventType.WindowOccluded, 0u)]
    public void Ignored_AddsNothing(SdlEventType type, uint windowId)
    {
        PlatformEventBuffer buffer = NewBuffer();

        // A scale source that knows window 0, so the id-0 rows are ignored for the id, not for a failed query.
        FakeScales scales = new(windowId, WindowScale, DisplayIdValue, DisplayScale);

        bool added = SdlEventTranslator.TryAdd(SdlEventOf(type, windowId), buffer, Timestamp, ref scales);

        Assert.False(added);
        Assert.Equal(0, buffer.Count);
        Assert.Equal(0, buffer.AsBatch().Events.Length);
    }

    [Fact]
    public void TryAdd_NullBuffer_Throws()
    {
        FakeScales scales = new(WindowIdValue, WindowScale, DisplayIdValue, DisplayScale);

        Assert.Throws<ArgumentNullException>(
            "buffer", () => SdlEventTranslator.TryAdd(SdlEventOf(SdlEventType.Quit, 0), null!, Timestamp, ref scales));
    }

    [Fact]
    public void TryAdd_FullBuffer_ThrowsAndAddsNothing()
    {
        // The pump checks HasRoom first, so a full buffer is a bug the buffer reports.
        PlatformEventBuffer buffer = NewBuffer();
        FakeScales scales = new(WindowIdValue, WindowScale, DisplayIdValue, DisplayScale);
        SdlEvent quit = SdlEventOf(SdlEventType.Quit, 0);
        for (int i = 0; i < buffer.Capacity; i++)
        {
            Assert.True(SdlEventTranslator.TryAdd(quit, buffer, i, ref scales));
        }

        Assert.Throws<InvalidOperationException>(
            () => SdlEventTranslator.TryAdd(SdlEventOf(SdlEventType.WindowShown, WindowIdValue), buffer, Timestamp, ref scales));
        Assert.Equal(buffer.Capacity, buffer.Count);
    }

    [Fact]
    public void Translate_KeepsOrderAndTimestamps()
    {
        PlatformEventBuffer buffer = NewBuffer();
        FakeScales scales = new(WindowIdValue, WindowScale, DisplayIdValue, DisplayScale);

        Assert.True(SdlEventTranslator.TryAdd(SdlEventOf(SdlEventType.WindowFocusGained, WindowIdValue), buffer, 30, ref scales));
        Assert.False(SdlEventTranslator.TryAdd(SdlEventOf(SdlEventType.GamepadAdded, WindowIdValue), buffer, 20, ref scales));
        Assert.True(SdlEventTranslator.TryAdd(SdlEventOf(SdlEventType.Quit, 0), buffer, 10, ref scales));

        PlatformEvent[] events = buffer.AsBatch().Events.ToArray();
        Assert.Equal(2, events.Length);
        Assert.Equal(new PlatformEventHeader(PlatformEventKind.WindowFocusGained, new WindowId(WindowIdValue), 30), events[0].Header);
        Assert.Equal(new PlatformEventHeader(PlatformEventKind.QuitRequested, default, 10), events[1].Header);
    }

    [Fact]
    public void Translate_WindowKinds_DoesNotAllocate()
    {
        SdlEventType[] types =
        [
            SdlEventType.Quit, SdlEventType.WindowShown, SdlEventType.WindowHidden, SdlEventType.WindowExposed,
            SdlEventType.WindowMoved, SdlEventType.WindowResized, SdlEventType.WindowPixelSizeChanged,
            SdlEventType.WindowMinimized, SdlEventType.WindowMaximized, SdlEventType.WindowRestored,
            SdlEventType.WindowMouseEnter, SdlEventType.WindowMouseLeave, SdlEventType.WindowFocusGained,
            SdlEventType.WindowFocusLost, SdlEventType.WindowCloseRequested, SdlEventType.WindowDisplayChanged,
            SdlEventType.WindowDisplayScaleChanged, SdlEventType.WindowOccluded, SdlEventType.DisplayAdded,
            SdlEventType.DisplayRemoved, SdlEventType.DisplayCurrentModeChanged, SdlEventType.DisplayContentScaleChanged,
            SdlEventType.GamepadAxisMotion, SdlEventType.DropFile,
        ];
        var events = new SdlEvent[types.Length];
        for (int i = 0; i < types.Length; i++)
        {
            events[i] = SdlEventOf(types[i], WindowIdValue);
        }

        PlatformEventBuffer buffer = new(1024, 2048);
        FakeScales scales = new(WindowIdValue, WindowScale, DisplayIdValue, DisplayScale);
        int added = TranslateEach(events, buffer, ref scales); // warm-up: JIT and first-call costs

        long before = GC.GetAllocatedBytesForCurrentThread();
        added += TranslateEach(events, buffer, ref scales);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.Equal(2 * 1000 * (types.Length - 2), added); // the last two are ignored
    }

    [Fact]
    public void SdlScaleSource_LiveWindowAndDisplay_ReadSdlScales()
    {
        using var variable = NativeEnvironmentVariable.Set("SDL_VIDEO_DRIVER", null);
        using SdlVideoSession session = StartDummy();
        WindowOptions options = new() { Title = "Platform.Sdl3.Tests", ContentSize = new(320, 200), Surface = WindowSurface.None };
        using SdlPlatformWindow window = session.CreateWindow(options).Value;
        uint primary = Sdl.PInvoke.GetPrimaryDisplay();
        SdlEventTranslator.SdlScaleSource source = default;

        // The dummy driver's display, and so its windows, are at scale 1 (as SdlDisplaysTests observes).
        Assert.True(source.TryGetWindowDisplayScale(window.Id, out float windowScale));
        Assert.Equal(window.DisplayScale, windowScale);
        Assert.Equal(1f, windowScale);
        Assert.True(source.TryGetDisplayContentScale(new DisplayId(primary), out float displayScale));
        Assert.Equal(1f, displayScale);
    }

    [Fact]
    public void SdlScaleSource_GoneWindowOrDisplay_Fails()
    {
        using var variable = NativeEnvironmentVariable.Set("SDL_VIDEO_DRIVER", null);
        using SdlVideoSession session = StartDummy();
        WindowOptions options = new() { Title = "Platform.Sdl3.Tests", ContentSize = new(320, 200), Surface = WindowSurface.None };
        SdlPlatformWindow window = session.CreateWindow(options).Value;
        WindowId closed = window.Id;
        window.Dispose();
        SdlEventTranslator.SdlScaleSource source = default;

        Assert.False(source.TryGetWindowDisplayScale(closed, out _));
        Assert.False(source.TryGetWindowDisplayScale(default, out _));
        Assert.False(source.TryGetDisplayContentScale(default, out _));

        // Display ids count up across SDL restarts, so an unknown id is derived from the primary's.
        Assert.False(source.TryGetDisplayContentScale(new DisplayId(Sdl.PInvoke.GetPrimaryDisplay() + 1), out _));
    }

    [Fact]
    public void SdlScaleSource_Queries_DoNotAllocate()
    {
        using var variable = NativeEnvironmentVariable.Set("SDL_VIDEO_DRIVER", null);
        using SdlVideoSession session = StartDummy();
        WindowOptions options = new() { Title = "Platform.Sdl3.Tests", ContentSize = new(320, 200), Surface = WindowSurface.None };
        using SdlPlatformWindow window = session.CreateWindow(options).Value;
        WindowId id = window.Id;
        DisplayId primary = new(Sdl.PInvoke.GetPrimaryDisplay());
        SdlEventTranslator.SdlScaleSource source = default;
        int read = QueryEach(source, id, primary); // warm-up

        long before = GC.GetAllocatedBytesForCurrentThread();
        read += QueryEach(source, id, primary);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.Equal(2 * 2 * 1000, read);
    }

    private static int QueryEach(SdlEventTranslator.SdlScaleSource source, WindowId window, DisplayId display)
    {
        int read = 0;
        for (int i = 0; i < 1000; i++)
        {
            read += source.TryGetWindowDisplayScale(window, out _) ? 1 : 0;
            read += source.TryGetDisplayContentScale(display, out _) ? 1 : 0;
        }

        return read;
    }

    private static int TranslateEach(SdlEvent[] events, PlatformEventBuffer buffer, ref FakeScales scales)
    {
        int added = 0;
        foreach (SdlEvent sdlEvent in events)
        {
            buffer.Clear();
            for (int i = 0; i < 1000; i++)
            {
                if (SdlEventTranslator.TryAdd(sdlEvent, buffer, i, ref scales))
                {
                    added++;
                }
            }
        }

        return added;
    }

    private static PlatformEventBuffer NewBuffer() => new(64, 2048);

    private static SdlVideoSession StartDummy()
    {
        PlatformOptions options = new() { Organization = "BrutalEngine", Application = "Platform.Sdl3.Tests", Headless = HeadlessMode.NoSurface };
        return SdlVideoSession.Start(options, null).Value;
    }

    // The SDL event a row describes. Window rows fill SDL's window view, display rows its display view; other types
    // only set the type, with the window view's bytes left non-zero so the union holds data where SDL would.
    private static SdlEvent SdlEventOf(SdlEventType type, uint windowId)
    {
        SdlEvent sdlEvent = default;
        if (type is >= SdlEventType.DisplayFirst and <= SdlEventType.DisplayLast)
        {
            sdlEvent.display = new SdlDisplayEvent { type = type, displayID = DisplayIdValue, data1 = Data1, data2 = Data2 };
        }
        else if (type == SdlEventType.WindowDisplayChanged)
        {
            // SDL sends the new display's id in data1 (SDL_video.c, SDL_CheckWindowDisplayChanged).
            sdlEvent.window = new SdlWindowEvent { type = type, windowID = windowId, data1 = (int)DisplayIdValue };
        }
        else
        {
            sdlEvent.window = new SdlWindowEvent { type = type, windowID = windowId, data1 = Data1, data2 = Data2 };
        }

        return sdlEvent;
    }

    // The event each kind should produce from SdlEventOf's inputs, built with the Platform factories.
    private static PlatformEvent Expected(PlatformEventKind kind)
    {
        PlatformEventHeader window = new(kind, new WindowId(WindowIdValue), Timestamp);
        PlatformEventHeader platform = new(kind, default, Timestamp);
        return kind switch
        {
            PlatformEventKind.QuitRequested => PlatformEvent.Create(platform),
            PlatformEventKind.WindowResized or PlatformEventKind.WindowPixelSizeChanged =>
                PlatformEvent.CreateSize(window, new WindowSize(Data1, Data2)),
            PlatformEventKind.WindowDisplayChanged => PlatformEvent.CreateDisplay(window, new DisplayId(DisplayIdValue)),
            PlatformEventKind.WindowDisplayScaleChanged => PlatformEvent.CreateScale(window, WindowScale),
            PlatformEventKind.DisplayAdded or PlatformEventKind.DisplayRemoved or PlatformEventKind.DisplayCurrentModeChanged =>
                PlatformEvent.CreateDisplay(platform, new DisplayId(DisplayIdValue)),
            PlatformEventKind.DisplayContentScaleChanged =>
                PlatformEvent.CreateDisplayScale(platform, new DisplayId(DisplayIdValue), DisplayScale),
            _ => PlatformEvent.Create(window),
        };
    }

    // Knows one window's and one display's scale; every other id fails, as a gone window or display does in SDL.
    private struct FakeScales(uint window, float windowScale, uint display, float displayScale) : SdlEventTranslator.IScaleSource
    {
        public int Queries { get; private set; }

        public bool TryGetWindowDisplayScale(WindowId id, out float scale)
        {
            Queries++;
            scale = id.Value == window ? windowScale : 0f;
            return id.Value == window;
        }

        public bool TryGetDisplayContentScale(DisplayId id, out float scale)
        {
            Queries++;
            scale = id.Value == display ? displayScale : 0f;
            return id.Value == display;
        }
    }
}
