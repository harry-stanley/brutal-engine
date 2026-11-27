using System;
using System.Collections.Generic;
using Brutal.SdlApi;
using CsCheck;
using Xunit;

namespace BrutalEngine.Platform.Sdl3.Tests;

// Every row pushes events into SDL's real queue on the dummy driver, so all of them are in the SDL collection.
[Collection(SdlNativeTests.Name)]
public sealed unsafe class SdlEventPumpTests
{
    // PlatformEventBuffer's smallest capacities, so a few events fill the buffer.
    private const int Capacity = 64;
    private const int TextCapacity = 2048;

    // SDL's whole event-type range (SDL_EVENT_FIRST to SDL_EVENT_LAST).
    private const uint FirstType = 0;
    private const uint LastType = 0xFFFF;

    // Literal inputs: distinct values, so a payload read from the wrong field shows.
    private const uint WindowIdValue = 7;
    private const uint DisplayIdValue = 5;

    // "héllo" and its NUL, on the pinned heap so pushed SDL events can point at it for the test run.
    private static readonly byte[] _hello = Pinned("h\u00E9llo\0"u8);

    [Fact]
    public void Pump_PushedEvents_InOrder()
    {
        using var variable = NativeEnvironmentVariable.Set("SDL_VIDEO_DRIVER", null);
        using SdlVideoSession session = StartDummy();
        ScriptedClock clock = new();
        PlatformEventBuffer buffer = new(Capacity, TextCapacity);
        SdlEventPump pump = new(buffer, clock);

        // One event of each payload shape, with an ignored user event in the middle.
        Push(new SdlEvent { type = SdlEventType.Quit });
        Push(new SdlEvent { window = new SdlWindowEvent { type = SdlEventType.WindowResized, windowID = WindowIdValue, data1 = 640, data2 = 480 } });
        Push(Key(SdlEventType.KeyDown));
        Push(Text(_hello));
        Push(Motion(10.5f));
        Push(new SdlEvent { user = new SdlUserEvent { type = (uint)SdlEventType.User, code = 1 } });
        Push(new SdlEvent { button = new SdlMouseButtonEvent { type = SdlEventType.MouseButtonDown, windowID = WindowIdValue, button = 3, clicks = 2, x = 1.5f, y = 2.5f, down = 1 } });
        Push(new SdlEvent { wheel = new SdlMouseWheelEvent { type = SdlEventType.MouseWheel, windowID = WindowIdValue, x = 0.5f, y = -2f, mouse_x = 3.5f, mouse_y = 4.5f } });
        Push(new SdlEvent { display = new SdlDisplayEvent { type = SdlEventType.DisplayAdded, displayID = DisplayIdValue } });
        Push(Key(SdlEventType.KeyUp));

        PlatformEventBatch batch = pump.Pump();

        // The clock is read once per event taken from SDL: the ignored user event takes timestamp 6.
        WindowId window = new(WindowIdValue);
        KeyEventData key = new(Scancode.Z, new Keycode('z'), KeyModifiers.None, false);
        PlatformEvent[] expected =
        [
            PlatformEvent.Create(new(PlatformEventKind.QuitRequested, default, 1)),
            PlatformEvent.CreateSize(new(PlatformEventKind.WindowResized, window, 2), new WindowSize(640, 480)),
            PlatformEvent.CreateKey(new(PlatformEventKind.KeyDown, window, 3), key),
            PlatformEvent.CreateText(new(PlatformEventKind.TextInput, window, 4), new TextEventData(0, 6)),
            PlatformEvent.CreateMouseMotion(new(PlatformEventKind.MouseMotion, window, 5), new MouseMotionData(10.5f, 0f, 0f, 0f, MouseButtons.None)),
            PlatformEvent.CreateMouseButton(new(PlatformEventKind.MouseButtonDown, window, 7), new MouseButtonData(MouseButton.Right, 2, 1.5f, 2.5f)),
            PlatformEvent.CreateMouseWheel(new(PlatformEventKind.MouseWheel, window, 8), new MouseWheelData(0.5f, -2f, 3.5f, 4.5f)),
            PlatformEvent.CreateDisplay(new(PlatformEventKind.DisplayAdded, default, 9), new DisplayId(DisplayIdValue)),
            PlatformEvent.CreateKey(new(PlatformEventKind.KeyUp, window, 10), key),
        ];
        Assert.Equal(expected, batch.Events.ToArray());
        Assert.Equal("h\u00E9llo"u8.ToArray(), batch.GetText(batch.Events[3]).ToArray());
        Assert.False(batch.HasDeferredEvents);
        Assert.Equal(0, buffer.DeferredPumpCount);
        Assert.Equal(10, clock.Reads);

        // Every event left SDL's queue, the ignored one too, and the next pump starts from an empty buffer.
        Assert.Equal(0, Sdl.PInvoke.HasEvents(FirstType, LastType));
        Assert.Empty(pump.Pump().Events.ToArray());
    }

    [Fact]
    public void Pump_Overflow_DefersAndKeepsOrder()
    {
        using var variable = NativeEnvironmentVariable.Set("SDL_VIDEO_DRIVER", null);
        using SdlVideoSession session = StartDummy();
        PlatformEventBuffer buffer = new(Capacity, TextCapacity);
        SdlEventPump pump = new(buffer, new ScriptedClock());
        PushMotions(0, 150);

        PlatformEventBatch first = pump.Pump();
        Assert.Equal(Range(0, 64), Positions(first));
        Assert.True(first.HasDeferredEvents);
        Assert.Equal(1, buffer.DeferredPumpCount);

        PlatformEventBatch second = pump.Pump();
        Assert.Equal(Range(64, 64), Positions(second));
        Assert.True(second.HasDeferredEvents);
        Assert.Equal(2, buffer.DeferredPumpCount);

        PlatformEventBatch third = pump.Pump();
        Assert.Equal(Range(128, 22), Positions(third));
        Assert.False(third.HasDeferredEvents);
        Assert.Equal(2, buffer.DeferredPumpCount);
        Assert.Equal(0, Sdl.PInvoke.HasEvents(FirstType, LastType));
    }

    [Fact]
    public void Pump_TextSpaceFull_DefersRest()
    {
        using var variable = NativeEnvironmentVariable.Set("SDL_VIDEO_DRIVER", null);
        using SdlVideoSession session = StartDummy();
        PlatformEventBuffer buffer = new(Capacity, TextCapacity);
        SdlEventPump pump = new(buffer, new ScriptedClock());

        // Three 600-byte texts: after two, 848 bytes are free, below the 1,024 HasRoom keeps, though 62 events fit.
        byte[] text = GC.AllocateArray<byte>(601, pinned: true);
        text.AsSpan(0, 600).Fill((byte)'a');
        Push(Text(text));
        Push(Text(text));
        Push(Text(text));

        PlatformEventBatch first = pump.Pump();
        Assert.Equal(2, first.Events.Length);
        Assert.True(first.HasDeferredEvents);

        PlatformEventBatch second = pump.Pump();
        Assert.Equal(1, second.Events.Length);
        Assert.Equal(600, second.GetText(second.Events[0]).Length);
        Assert.False(second.HasDeferredEvents);
        Assert.Equal(1, buffer.DeferredPumpCount);
        Assert.Equal(0, buffer.TruncatedTextCount);
    }

    [Fact]
    public void Pump_RandomCounts_LoseNothing()
    {
        using var variable = NativeEnvironmentVariable.Set("SDL_VIDEO_DRIVER", null);
        using SdlVideoSession session = StartDummy();

        // Counts near whole multiples of the capacity (exactly full pumps) half the time, any count up to 300 otherwise.
        Gen<(int Capacity, int Count)> rows = Gen.Int[64, 80].SelectMany(capacity => Gen.OneOf(
            Gen.Int[0, 300],
            Gen.Select(Gen.Int[0, 3], Gen.Int[-1, 1], (multiple, offset) => Math.Max(0, (multiple * capacity) + offset)))
            .Select(count => (capacity, count)));

        rows.Sample(
            row =>
            {
                Sdl.PInvoke.FlushEvents(FirstType, LastType);
                PlatformEventBuffer buffer = new(row.Capacity, TextCapacity);
                SdlEventPump pump = new(buffer, new ScriptedClock());
                PushMotions(0, row.Count);

                List<int> received = [];
                int pumps = 0;
                bool deferred;
                do
                {
                    // Bounded, so a pump that never stops deferring fails instead of spinning.
                    Assert.True(++pumps <= (row.Count / row.Capacity) + 2, "The pump kept deferring.");
                    PlatformEventBatch batch = pump.Pump();
                    deferred = batch.HasDeferredEvents;
                    Assert.Equal(deferred ? row.Capacity : batch.Events.Length, batch.Events.Length);
                    received.AddRange(Positions(batch));
                }
                while (deferred);

                Assert.Equal(Range(0, row.Count), received.ToArray());
                Assert.Equal(row.Count == 0 ? 0 : (row.Count - 1) / row.Capacity, buffer.DeferredPumpCount);
                Assert.Equal(0, Sdl.PInvoke.HasEvents(FirstType, LastType));
            },
            iter: 60,
            threads: 1);
    }

    [Fact]
    public void Pump_ExactlyFull_NoDeferral()
    {
        using var variable = NativeEnvironmentVariable.Set("SDL_VIDEO_DRIVER", null);
        using SdlVideoSession session = StartDummy();
        PlatformEventBuffer buffer = new(Capacity, TextCapacity);
        SdlEventPump pump = new(buffer, new ScriptedClock());
        PushMotions(0, Capacity);

        PlatformEventBatch batch = pump.Pump();

        Assert.Equal(Range(0, Capacity), Positions(batch));
        Assert.False(batch.HasDeferredEvents);
        Assert.Equal(0, buffer.DeferredPumpCount);
        Assert.Empty(pump.Pump().Events.ToArray());
    }

    [Fact]
    public void Pump_LeftoverSentinel_NoDeferralNoDelay()
    {
        using var variable = NativeEnvironmentVariable.Set("SDL_VIDEO_DRIVER", null);
        using SdlVideoSession session = StartDummy();
        PlatformEventBuffer buffer = new(Capacity, TextCapacity);
        SdlEventPump pump = new(buffer, new ScriptedClock());

        // A polling pump that took exactly what was queued leaves SDL's poll sentinel at the front of the queue.
        PushMotions(0, 3);
        SdlEvent polled;
        for (int i = 0; i < 3; i++)
        {
            // Sound: polled is a local of this frame, which outlives the call.
            Assert.NotEqual(0, Sdl.PInvoke.PollEvent(&polled));
        }

        Assert.NotEqual(0, Sdl.PInvoke.HasEvent((uint)SdlEventType.PollSentinel));
        PushMotions(10, 2);

        PlatformEventBatch batch = pump.Pump();

        // Both events arrive in this pump, not the next, and the sentinel is gone.
        int[] expected = [10, 11];
        Assert.Equal(expected, Positions(batch));
        Assert.False(batch.HasDeferredEvents);
        Assert.Equal(0, buffer.DeferredPumpCount);
        Assert.Equal(0, Sdl.PInvoke.HasEvents(FirstType, LastType));
    }

    [Fact]
    public void Pump_SentinelBehindQueuedEvents_ExactlyFull_NoDeferral()
    {
        using var variable = NativeEnvironmentVariable.Set("SDL_VIDEO_DRIVER", null);
        using SdlVideoSession session = StartDummy();
        PlatformEventBuffer buffer = new(Capacity, TextCapacity);
        SdlEventPump pump = new(buffer, new ScriptedClock());

        // SDL_WaitEventTimeout takes the first event and queues its sentinel behind the 64 still queued.
        PushMotions(0, Capacity + 1);
        SdlEvent waited;

        // Sound: waited is a local of this frame, which outlives the call.
        Assert.NotEqual(0, Sdl.PInvoke.WaitEventTimeout(&waited, 1));
        Assert.NotEqual(0, Sdl.PInvoke.HasEvent((uint)SdlEventType.PollSentinel));

        PlatformEventBatch batch = pump.Pump();

        // Only the sentinel is left, which isn't an event: no deferral.
        Assert.Equal(Range(1, Capacity), Positions(batch));
        Assert.False(batch.HasDeferredEvents);
        Assert.Equal(0, buffer.DeferredPumpCount);
        PlatformEventBatch next = pump.Pump();
        Assert.Empty(next.Events.ToArray());
        Assert.False(next.HasDeferredEvents);
        Assert.Equal(0, Sdl.PInvoke.HasEvents(FirstType, LastType));
    }

    [Fact]
    public void Pump_Only_DoesNotAllocate()
    {
        using var variable = NativeEnvironmentVariable.Set("SDL_VIDEO_DRIVER", null);
        using SdlVideoSession session = StartDummy();

        // A live window and display, so both scale events read a scale through SDL and are kept.
        WindowOptions options = new() { Title = "Platform.Sdl3.Tests", ContentSize = new(320, 200), Surface = WindowSurface.None };
        using SdlPlatformWindow window = session.CreateWindow(options).Value;
        Sdl.PInvoke.FlushEvents(FirstType, LastType);
        SdlEvent[] events = EveryKind(window.Id.Value, Sdl.PInvoke.GetPrimaryDisplay());
        PlatformEventBuffer buffer = new(1024, 8192);
        SdlEventPump pump = new(buffer, new ScriptedClock());
        long checksum = PumpEach(pump, events, 1); // warm-up: JIT and first-call costs

        long before = GC.GetAllocatedBytesForCurrentThread();
        checksum += PumpEach(pump, events, 1000);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);

        // Every mapped type adds one event per round; the two ignored types add none.
        Assert.Equal(1001L * (events.Length - 2) * 1000, checksum);
        Assert.Equal(0, buffer.DeferredPumpCount);
        Assert.Equal(0, buffer.TruncatedTextCount);
    }

    [Fact]
    public void Pump_AfterVideoStopped_ThrowsWithSdlError()
    {
        using var variable = NativeEnvironmentVariable.Set("SDL_VIDEO_DRIVER", null);
        SdlVideoSession session = StartDummy();
        PlatformEventBuffer buffer = new(Capacity, TextCapacity);
        SdlEventPump pump = new(buffer, new ScriptedClock());
        session.Dispose();

        InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(() => pump.Pump());

        Assert.StartsWith("SDL_PeepEvents failed: ", thrown.Message, StringComparison.Ordinal);
        Assert.EndsWith("pump only while the video session is live.", thrown.Message, StringComparison.Ordinal);
        Assert.Equal(0, buffer.Count);
        Assert.Throws<InvalidOperationException>(() => pump.Pump());
    }

    [Fact]
    public void Constructor_NullArguments_Throw()
    {
        PlatformEventBuffer buffer = new(Capacity, TextCapacity);

        Assert.Equal("buffer", Assert.Throws<ArgumentNullException>(() => new SdlEventPump(null!, new ScriptedClock())).ParamName);
        Assert.Equal("clock", Assert.Throws<ArgumentNullException>(() => new SdlEventPump(buffer, null!)).ParamName);
    }

    // Pushes every type of the round, pumps, and folds each event's payload, read through its own view, into a
    // checksum: 1,000 times the round's number plus one per event, so a dropped or misread event changes it.
    private static long PumpEach(SdlEventPump pump, SdlEvent[] events, int rounds)
    {
        long checksum = 0;
        for (int round = 0; round < rounds; round++)
        {
            for (int i = 0; i < events.Length; i++)
            {
                SdlEvent sdlEvent = events[i];

                // Sound: sdlEvent is a local of this frame; SDL copies the event into its queue.
                if (Sdl.PInvoke.PushEvent(&sdlEvent) == 0)
                {
                    return -1;
                }
            }

            PlatformEventBatch batch = pump.Pump();
            foreach (ref readonly PlatformEvent platformEvent in batch.Events)
            {
                checksum += 1000 + Read(batch, platformEvent);
            }
        }

        return checksum;
    }

    // Reads the payload of every kind through the view it has, so each kind's own read path runs in the window. Each
    // read folds into 0 (no payload is NaN), so the checksum stays a count.
    private static int Read(PlatformEventBatch batch, in PlatformEvent platformEvent)
    {
        float value = platformEvent.Kind switch
        {
            PlatformEventKind.KeyDown or PlatformEventKind.KeyUp => (float)platformEvent.Key.Scancode,
            PlatformEventKind.TextInput => batch.GetText(platformEvent).Length,
            PlatformEventKind.MouseMotion => platformEvent.MouseMotion.X,
            PlatformEventKind.MouseButtonDown or PlatformEventKind.MouseButtonUp => platformEvent.MouseButton.X,
            PlatformEventKind.MouseWheel => platformEvent.MouseWheel.Y,
            PlatformEventKind.WindowResized or PlatformEventKind.WindowPixelSizeChanged => platformEvent.Size.Width,
            PlatformEventKind.WindowDisplayChanged or PlatformEventKind.DisplayAdded or PlatformEventKind.DisplayRemoved
                or PlatformEventKind.DisplayCurrentModeChanged => platformEvent.Display.Value,
            PlatformEventKind.WindowDisplayScaleChanged or PlatformEventKind.DisplayContentScaleChanged => platformEvent.Scale,
            _ => platformEvent.Timestamp,
        };
        return float.IsNaN(value) ? 1 : 0;
    }

    // One SDL event of every type the translator maps (29), then two it ignores, for a live window and display.
    private static SdlEvent[] EveryKind(uint windowId, uint displayId)
    {
        SdlEventType[] windowTypes =
        [
            SdlEventType.WindowShown, SdlEventType.WindowHidden, SdlEventType.WindowExposed, SdlEventType.WindowMoved,
            SdlEventType.WindowResized, SdlEventType.WindowPixelSizeChanged, SdlEventType.WindowMinimized,
            SdlEventType.WindowMaximized, SdlEventType.WindowRestored, SdlEventType.WindowMouseEnter,
            SdlEventType.WindowMouseLeave, SdlEventType.WindowFocusGained, SdlEventType.WindowFocusLost,
            SdlEventType.WindowCloseRequested, SdlEventType.WindowDisplayChanged, SdlEventType.WindowDisplayScaleChanged,
            SdlEventType.WindowOccluded,
        ];
        SdlEventType[] displayTypes =
        [
            SdlEventType.DisplayAdded, SdlEventType.DisplayRemoved, SdlEventType.DisplayCurrentModeChanged,
            SdlEventType.DisplayContentScaleChanged,
        ];
        List<SdlEvent> events = [new SdlEvent { type = SdlEventType.Quit }];
        foreach (SdlEventType type in windowTypes)
        {
            int data1 = type == SdlEventType.WindowDisplayChanged ? (int)displayId : 320;
            events.Add(new SdlEvent { window = new SdlWindowEvent { type = type, windowID = windowId, data1 = data1, data2 = 200 } });
        }

        foreach (SdlEventType type in displayTypes)
        {
            events.Add(new SdlEvent { display = new SdlDisplayEvent { type = type, displayID = displayId } });
        }

        events.Add(Key(SdlEventType.KeyDown));
        events.Add(Key(SdlEventType.KeyUp));
        events.Add(Text(_hello));
        events.Add(Motion(1.5f));
        events.Add(new SdlEvent { button = new SdlMouseButtonEvent { type = SdlEventType.MouseButtonDown, windowID = windowId, button = 1, clicks = 1, down = 1 } });
        events.Add(new SdlEvent { button = new SdlMouseButtonEvent { type = SdlEventType.MouseButtonUp, windowID = windowId, button = 1, clicks = 1 } });
        events.Add(new SdlEvent { wheel = new SdlMouseWheelEvent { type = SdlEventType.MouseWheel, windowID = windowId, y = 1f, direction = SdlMouseWheelDirection.Flipped } });
        events.Add(new SdlEvent { type = SdlEventType.GamepadAxisMotion });
        events.Add(new SdlEvent { type = SdlEventType.DropFile });
        return [.. events];
    }

    private static SdlEvent Key(SdlEventType type) => new()
    {
        key = new SdlKeyboardEvent
        {
            type = type,
            windowID = WindowIdValue,
            scancode = SdlScancode.Z,
            key = (SdlKeycode)'z',
            down = type == SdlEventType.KeyDown ? (byte)1 : (byte)0,
        },
    };

    private static SdlEvent Text(byte[] pinnedText)
    {
        fixed (byte* text = pinnedText)
        {
            // Sound: the array is on the pinned heap, so the pointer stays valid after the fixed block; SDL keeps a
            // pushed text pointer as given.
            return new SdlEvent { text = new SdlTextInputEvent { type = SdlEventType.TextInput, windowID = WindowIdValue, text = text } };
        }
    }

    private static SdlEvent Motion(float x) =>
        new() { motion = new SdlMouseMotionEvent { type = SdlEventType.MouseMotion, windowID = WindowIdValue, x = x } };

    // Pushes count motion events whose x positions count up from first, so the batches show their order.
    private static void PushMotions(int first, int count)
    {
        for (int i = 0; i < count; i++)
        {
            Push(Motion(first + i));
        }
    }

    private static void Push(SdlEvent sdlEvent)
    {
        // Sound: sdlEvent is a parameter of this frame; SDL copies the event into its queue.
        Assert.True(Sdl.PInvoke.PushEvent(&sdlEvent) != 0, Sdl.PInvoke.GetError());
    }

    private static int[] Positions(PlatformEventBatch batch)
    {
        int[] positions = new int[batch.Events.Length];
        for (int i = 0; i < positions.Length; i++)
        {
            positions[i] = (int)batch.Events[i].MouseMotion.X;
        }

        return positions;
    }

    private static int[] Range(int first, int count)
    {
        int[] values = new int[count];
        for (int i = 0; i < count; i++)
        {
            values[i] = first + i;
        }

        return values;
    }

    // A copy of the bytes on the pinned heap, which the garbage collector never moves.
    private static byte[] Pinned(ReadOnlySpan<byte> bytes)
    {
        byte[] pinned = GC.AllocateArray<byte>(bytes.Length, pinned: true);
        bytes.CopyTo(pinned);
        return pinned;
    }

    // Starts SDL video on the dummy driver and empties SDL's queue, so each row sees only the events it pushes.
    private static SdlVideoSession StartDummy()
    {
        PlatformOptions options = new() { Organization = "BrutalEngine", Application = "Platform.Sdl3.Tests", Headless = HeadlessMode.NoSurface };
        SdlVideoSession session = SdlVideoSession.Start(options, null).Value;
        Sdl.PInvoke.PumpEvents();
        Sdl.PInvoke.FlushEvents(FirstType, LastType);
        return session;
    }

    // Returns 1, 2, 3, ... and counts the reads, so a row can tell which event took which timestamp.
    private sealed class ScriptedClock : TimeProvider
    {
        public int Reads { get; private set; }

        public override long TimestampFrequency => 1000;

        public override long GetTimestamp() => ++Reads;
    }
}
