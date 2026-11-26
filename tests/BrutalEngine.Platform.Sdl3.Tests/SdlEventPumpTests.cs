using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Brutal.SdlApi;
using BrutalEngine.Core;
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
    private static readonly byte[] _world = Pinned("w\u00F6rld\0"u8);

    // A lookup that knows no window and records nothing, for rows that don't look at the hooks.
    private static readonly LiveWindows _noWindows = new();

    private static WindowOptions NoSurfaceWindow { get; } = new() { Title = "Platform.Sdl3.Tests", ContentSize = new(320, 200), Surface = WindowSurface.None };

    [Fact]
    public void Pump_PushedEvents_InOrder()
    {
        using var variable = NativeEnvironmentVariable.Set("SDL_VIDEO_DRIVER", null);
        using SdlVideoSession session = StartDummy();
        ScriptedClock clock = new();
        SdlEventPump pump = new(Capacity, TextCapacity, clock, _noWindows);
        PlatformEventBuffer buffer = pump.Buffer;

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
        SdlEventPump pump = new(Capacity, TextCapacity, new ScriptedClock(), _noWindows);
        PlatformEventBuffer buffer = pump.Buffer;
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
        SdlEventPump pump = new(Capacity, TextCapacity, new ScriptedClock(), _noWindows);
        PlatformEventBuffer buffer = pump.Buffer;

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
                SdlEventPump pump = new(row.Capacity, TextCapacity, new ScriptedClock(), _noWindows);
                PlatformEventBuffer buffer = pump.Buffer;
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
        SdlEventPump pump = new(Capacity, TextCapacity, new ScriptedClock(), _noWindows);
        PlatformEventBuffer buffer = pump.Buffer;
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
        SdlEventPump pump = new(Capacity, TextCapacity, new ScriptedClock(), _noWindows);
        PlatformEventBuffer buffer = pump.Buffer;

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
        SdlEventPump pump = new(Capacity, TextCapacity, new ScriptedClock(), _noWindows);
        PlatformEventBuffer buffer = pump.Buffer;

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
        SdlEventPump pump = new(1024, 8192, new ScriptedClock(), _noWindows);
        PlatformEventBuffer buffer = pump.Buffer;
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
        SdlEventPump pump = new(Capacity, TextCapacity, new ScriptedClock(), _noWindows);
        PlatformEventBuffer buffer = pump.Buffer;
        session.Dispose();

        InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(() => pump.Pump());

        Assert.StartsWith("SDL_PeepEvents failed: ", thrown.Message, StringComparison.Ordinal);
        Assert.EndsWith("pump only while the video session is live.", thrown.Message, StringComparison.Ordinal);
        Assert.Equal(0, buffer.Count);
        Assert.Throws<InvalidOperationException>(() => pump.Pump());
    }

    [Fact]
    public void Constructor_InvalidArguments_Throw()
    {
        Assert.Equal("clock", Assert.Throws<ArgumentNullException>(() => new SdlEventPump(Capacity, TextCapacity, null!, _noWindows)).ParamName);
        Assert.Equal("windows", Assert.Throws<ArgumentNullException>(() => new SdlEventPump(Capacity, TextCapacity, new ScriptedClock(), null!)).ParamName);
        Assert.Equal("eventCapacity", Assert.Throws<ArgumentOutOfRangeException>(() => new SdlEventPump(Capacity - 1, TextCapacity, new ScriptedClock(), _noWindows)).ParamName);
        Assert.Equal("textCapacityBytes", Assert.Throws<ArgumentOutOfRangeException>(() => new SdlEventPump(Capacity, TextCapacity - 1, new ScriptedClock(), _noWindows)).ParamName);
    }

    [Fact]
    public void WaitForEvents_PushedText_HandedOutFirstWithText()
    {
        using var variable = NativeEnvironmentVariable.Set("SDL_VIDEO_DRIVER", null);
        using SdlVideoSession session = StartDummy();
        ScriptedClock clock = new();
        SdlEventPump pump = new(Capacity, TextCapacity, clock, _noWindows);
        byte[] source = Pinned("h\u00E9llo\0"u8);
        Push(Text(source));
        Push(Motion(1f));

        Assert.True(pump.WaitForEvents(TimeSpan.FromSeconds(30)));

        // SDL keeps a pushed text pointer as given, so overwriting the source shows whether the wait copied the text.
        source.AsSpan(0, source.Length - 1).Fill((byte)'X');
        Assert.Equal(1, clock.Reads);
        PlatformEventBatch batch = pump.Pump();

        // The held event comes first, with the wait's timestamp; the queued motion follows with the pump's.
        WindowId window = new(WindowIdValue);
        PlatformEvent[] expected =
        [
            PlatformEvent.CreateText(new(PlatformEventKind.TextInput, window, 1), new TextEventData(0, 6)),
            PlatformEvent.CreateMouseMotion(new(PlatformEventKind.MouseMotion, window, 2), new MouseMotionData(1f, 0f, 0f, 0f, MouseButtons.None)),
        ];
        Assert.Equal(expected, batch.Events.ToArray());
        Assert.Equal("h\u00E9llo"u8.ToArray(), batch.GetText(batch.Events[0]).ToArray());
        Assert.Empty(pump.Pump().Events.ToArray());
    }

    [Fact]
    public void WaitForEvents_Timeout_ReturnsFalse()
    {
        using var variable = NativeEnvironmentVariable.Set("SDL_VIDEO_DRIVER", null);
        using SdlVideoSession session = StartDummy();
        ScriptedClock clock = new();
        SdlEventPump pump = new(Capacity, TextCapacity, clock, _noWindows);

        // Zero only checks; one tick rounds up to a 1 ms wait. Nothing is queued, so each times out.
        Assert.False(pump.WaitForEvents(TimeSpan.Zero));
        Assert.False(pump.WaitForEvents(TimeSpan.FromTicks(1)));
        Assert.False(pump.WaitForEvents(TimeSpan.FromMilliseconds(1)));

        Assert.Equal(0, clock.Reads);
        Assert.Empty(pump.Pump().Events.ToArray());
    }

    [Fact]
    public void WaitForEvents_ZeroTimeout_TakesEventBehindSentinel()
    {
        using var variable = NativeEnvironmentVariable.Set("SDL_VIDEO_DRIVER", null);
        using SdlVideoSession session = StartDummy();
        SdlEventPump pump = new(Capacity, TextCapacity, new ScriptedClock(), _noWindows);

        // SDL's poll sentinel at the front, an event behind it: SDL_WaitEventTimeout(0) would stop at the sentinel.
        PushMotions(0, 1);
        SdlEvent polled;

        // Sound: polled is a local of this frame, which outlives the call.
        Assert.NotEqual(0, Sdl.PInvoke.PollEvent(&polled));
        Assert.NotEqual(0, Sdl.PInvoke.HasEvent((uint)SdlEventType.PollSentinel));
        PushMotions(5, 1);

        Assert.True(pump.WaitForEvents(TimeSpan.Zero));

        int[] expected = [5];
        Assert.Equal(expected, Positions(pump.Pump()));
    }

    [Fact]
    public void WaitForEvents_CurrentBatchUnchanged()
    {
        using var variable = NativeEnvironmentVariable.Set("SDL_VIDEO_DRIVER", null);
        using SdlVideoSession session = StartDummy();
        SdlEventPump pump = new(Capacity, TextCapacity, new ScriptedClock(), _noWindows);
        Push(Text(_hello));
        Push(Motion(1f));
        PlatformEventBatch batch = pump.Pump();
        PlatformEvent[] events = batch.Events.ToArray();
        byte[] text = batch.GetText(batch.Events[0]).ToArray();
        Push(Text(_world));

        Assert.True(pump.WaitForEvents(TimeSpan.FromSeconds(30)));

        // The batch the host holds, and the buffer behind it, are as the pump left them.
        Assert.Equal(events, batch.Events.ToArray());
        Assert.Equal(text, batch.GetText(batch.Events[0]).ToArray());
        Assert.Equal(2, pump.Buffer.Count);
        Assert.Equal(events, pump.Buffer.AsBatch().Events.ToArray());
        Assert.Equal(text, pump.Buffer.AsBatch().GetText(events[0]).ToArray());

        PlatformEventBatch next = pump.Pump();
        Assert.Equal("w\u00F6rld"u8.ToArray(), next.GetText(Assert.Single(next.Events.ToArray())).ToArray());
    }

    // Rows: a text cut inside 3-byte code points; exactly the capacity (kept whole); one byte over (cut and counted,
    // so the held copy kept more than the capacity); 4-byte code points ending on the capacity; 5-byte units, so byte
    // capacity + 4 is a continuation byte and the held copy steps back to a whole code point; exactly capacity + 4
    // bytes (the held copy's limit, kept whole there and cut by the buffer).
    [Theory]
    [InlineData("\u20AC", 1000, 2046, 1)]
    [InlineData("a", 2048, 2048, 0)]
    [InlineData("a", 2049, 2048, 1)]
    [InlineData("\U0001F600", 600, 2048, 1)]
    [InlineData("\u20ACaa", 700, 2048, 1)]
    [InlineData("a", 2052, 2048, 1)]
    public void WaitForEvents_LongText_CutAndCountedInMainBuffer(string unit, int repeat, int expectedLength, int expectedCut)
    {
        using var variable = NativeEnvironmentVariable.Set("SDL_VIDEO_DRIVER", null);
        using SdlVideoSession session = StartDummy();
        SdlEventPump pump = new(Capacity, TextCapacity, new ScriptedClock(), _noWindows);
        byte[] source = PinnedText(string.Concat(Enumerable.Repeat(unit, repeat)));
        byte[] original = source.AsSpan(0, source.Length - 1).ToArray();
        Push(Text(source));

        Assert.True(pump.WaitForEvents(TimeSpan.FromSeconds(30)));
        source.AsSpan(0, source.Length - 1).Fill((byte)'X');
        PlatformEventBatch batch = pump.Pump();

        PlatformEvent textEvent = Assert.Single(batch.Events.ToArray());
        Assert.Equal(original.AsSpan(0, expectedLength).ToArray(), batch.GetText(textEvent).ToArray());
        Assert.Equal(expectedCut, pump.Buffer.TruncatedTextCount);
    }

    [Theory]
    [InlineData(new byte[] { 0xC3, 0x28, 0 }, false)]
    [InlineData(new byte[] { (byte)'a', 0xFF, 0 }, false)]
    [InlineData(new byte[] { 0 }, true)]
    public void WaitForEvents_InvalidOrNullText_IgnoredLikePumped(byte[] bytes, bool isNull)
    {
        using var variable = NativeEnvironmentVariable.Set("SDL_VIDEO_DRIVER", null);
        using SdlVideoSession session = StartDummy();
        SdlEventPump pump = new(Capacity, TextCapacity, new ScriptedClock(), _noWindows);
        byte[] source = Pinned(bytes);
        SdlEvent textEvent = isNull ? new SdlEvent { text = new SdlTextInputEvent { type = SdlEventType.TextInput, windowID = WindowIdValue } } : Text(source);
        Push(textEvent);
        Push(textEvent);

        // The first is waited for and held, the second pumped: both are ignored, neither throws.
        Assert.True(pump.WaitForEvents(TimeSpan.FromSeconds(30)));
        PlatformEventBatch batch = pump.Pump();

        Assert.Empty(batch.Events.ToArray());
        Assert.Equal(0, pump.Buffer.TruncatedTextCount);
        Assert.Equal(0, Sdl.PInvoke.HasEvents(FirstType, LastType));
    }

    [Fact]
    public void WaitForEvents_InvalidByteBeyondCopiedText_Ignored()
    {
        using var variable = NativeEnvironmentVariable.Set("SDL_VIDEO_DRIVER", null);
        using SdlVideoSession session = StartDummy();
        SdlEventPump pump = new(Capacity, TextCapacity, new ScriptedClock(), _noWindows);

        // Valid up to well past the capacity plus 4 bytes the wait keeps, then a byte that is never valid UTF-8: the
        // whole text is checked, as the translator checks a pumped one, not only the copied part.
        byte[] source = GC.AllocateArray<byte>(TextCapacity + 101, pinned: true);
        source.AsSpan(0, TextCapacity + 99).Fill((byte)'a');
        source[TextCapacity + 99] = 0xFF;
        Push(Text(source));
        Push(Text(source));

        Assert.True(pump.WaitForEvents(TimeSpan.FromSeconds(30)));
        PlatformEventBatch batch = pump.Pump();

        Assert.Empty(batch.Events.ToArray());
        Assert.Equal(0, pump.Buffer.TruncatedTextCount);
        Assert.Equal(0, Sdl.PInvoke.HasEvents(FirstType, LastType));
    }

    [Fact]
    public void WaitForEvents_EventAlreadyHeld_ReturnsTrueAndKeepsIt()
    {
        using var variable = NativeEnvironmentVariable.Set("SDL_VIDEO_DRIVER", null);
        using SdlVideoSession session = StartDummy();
        ScriptedClock clock = new();
        SdlEventPump pump = new(Capacity, TextCapacity, clock, _noWindows);
        PushMotions(1, 2);

        Assert.True(pump.WaitForEvents(TimeSpan.FromSeconds(30)));
        Assert.True(pump.WaitForEvents(TimeSpan.FromSeconds(30)));

        // The second call took nothing from SDL and didn't replace the held event.
        Assert.Equal(1, clock.Reads);
        int[] expected = [1, 2];
        Assert.Equal(expected, Positions(pump.Pump()));
    }

    [Fact]
    public void WaitForEvents_HeldEventFillsSlot_RestDeferredInOrder()
    {
        using var variable = NativeEnvironmentVariable.Set("SDL_VIDEO_DRIVER", null);
        using SdlVideoSession session = StartDummy();
        SdlEventPump pump = new(Capacity, TextCapacity, new ScriptedClock(), _noWindows);
        PushMotions(0, Capacity + 1);

        Assert.True(pump.WaitForEvents(TimeSpan.FromSeconds(30)));

        // The held event takes the first slot, so one queued event is left for the next pump.
        PlatformEventBatch first = pump.Pump();
        Assert.Equal(Range(0, Capacity), Positions(first));
        Assert.True(first.HasDeferredEvents);
        PlatformEventBatch second = pump.Pump();
        int[] rest = [Capacity];
        Assert.Equal(rest, Positions(second));
        Assert.False(second.HasDeferredEvents);
        Assert.Equal(1, pump.Buffer.DeferredPumpCount);
    }

    // Each is negative other than Timeout.InfiniteTimeSpan (-1 ms), or above int.MaxValue milliseconds.
    [Theory]
    [InlineData(-1L)]
    [InlineData(-20_000L)]
    [InlineData(long.MinValue)]
    [InlineData((int.MaxValue * TimeSpan.TicksPerMillisecond) + 1)]
    [InlineData(long.MaxValue)]
    public void WaitForEvents_OutOfRangeTimeout_ThrowsBeforeAnyEffect(long ticks)
    {
        using var variable = NativeEnvironmentVariable.Set("SDL_VIDEO_DRIVER", null);
        using SdlVideoSession session = StartDummy();
        ScriptedClock clock = new();
        SdlEventPump pump = new(Capacity, TextCapacity, clock, _noWindows);
        PushMotions(1, 1);

        ArgumentOutOfRangeException thrown = Assert.Throws<ArgumentOutOfRangeException>(() => pump.WaitForEvents(TimeSpan.FromTicks(ticks)));

        Assert.Equal("timeout", thrown.ParamName);
        Assert.Equal(0, clock.Reads);
        Assert.NotEqual(0, Sdl.PInvoke.HasEvents(FirstType, LastType));
        int[] expected = [1];
        Assert.Equal(expected, Positions(pump.Pump()));
    }

    // The ends of the range: zero, one tick, int.MaxValue milliseconds and Timeout.InfiniteTimeSpan. An event is queued,
    // so every wait returns at once.
    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(int.MaxValue * TimeSpan.TicksPerMillisecond)]
    [InlineData(-TimeSpan.TicksPerMillisecond)]
    public void WaitForEvents_TimeoutInRange_ReceivesQueuedEvent(long ticks)
    {
        using var variable = NativeEnvironmentVariable.Set("SDL_VIDEO_DRIVER", null);
        using SdlVideoSession session = StartDummy();
        SdlEventPump pump = new(Capacity, TextCapacity, new ScriptedClock(), _noWindows);
        PushMotions(3, 1);

        Assert.True(pump.WaitForEvents(TimeSpan.FromTicks(ticks)));

        int[] expected = [3];
        Assert.Equal(expected, Positions(pump.Pump()));
    }

    // Rounded up to whole milliseconds, so a wait never ends early: each side of every millisecond boundary.
    [Theory]
    [InlineData(0L, 0)]
    [InlineData(1L, 1)]
    [InlineData(TimeSpan.TicksPerMillisecond - 1, 1)]
    [InlineData(TimeSpan.TicksPerMillisecond, 1)]
    [InlineData(TimeSpan.TicksPerMillisecond + 1, 2)]
    [InlineData((int.MaxValue - 1) * TimeSpan.TicksPerMillisecond + 1, int.MaxValue)]
    [InlineData(int.MaxValue * TimeSpan.TicksPerMillisecond, int.MaxValue)]
    [InlineData(-TimeSpan.TicksPerMillisecond, -1)]
    public void ToMilliseconds_RoundsUpToWholeMilliseconds(long ticks, int expected)
    {
        Assert.Equal(expected, SdlEventPump.ToMilliseconds(TimeSpan.FromTicks(ticks)));
    }

    [Fact]
    public void WaitForEvents_AfterVideoStopped_Throws()
    {
        using var variable = NativeEnvironmentVariable.Set("SDL_VIDEO_DRIVER", null);
        SdlVideoSession session = StartDummy();
        SdlEventPump pump = new(Capacity, TextCapacity, new ScriptedClock(), _noWindows);
        session.Dispose();

        // A stopped queue makes SDL_WaitEventTimeout return false, like a timeout; the pump must say why instead. SDL
        // sets no error text for the failed check, so an earlier failure's text (here a bad display) isn't carried.
        const string Expected = "SDL_PeepEvents failed: SDL's event queue isn't running: wait only while the video session is live.";
        Assert.Null(Sdl.PInvoke.GetDisplayName(12345));
        InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(() => pump.WaitForEvents(TimeSpan.FromMilliseconds(1)));

        Assert.Equal(Expected, thrown.Message);
        Assert.Equal(Expected, Assert.Throws<InvalidOperationException>(() => pump.WaitForEvents(TimeSpan.Zero)).Message);
        Assert.Equal(0, pump.Buffer.Count);
    }

    [Fact]
    public void WaitForEvents_DoesNotAllocate()
    {
        using var variable = NativeEnvironmentVariable.Set("SDL_VIDEO_DRIVER", null);
        using SdlVideoSession session = StartDummy();
        SdlEventPump pump = new(1024, 8192, new ScriptedClock(), _noWindows);
        SdlEvent text = Text(_hello);
        int handed = WaitEach(pump, text, 1); // warm-up: JIT and first-call costs

        long before = GC.GetAllocatedBytesForCurrentThread();
        handed += WaitEach(pump, text, 1000);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.Equal(1001, handed);
    }

    // Per round: a zero wait that times out, a wait that receives a pushed text event, and the pump that hands it out.
    private static int WaitEach(SdlEventPump pump, SdlEvent text, int rounds)
    {
        int handed = 0;
        for (int round = 0; round < rounds; round++)
        {
            bool timedOut = !pump.WaitForEvents(TimeSpan.Zero);

            // Sound: text is a parameter of this frame; SDL copies the event into its queue.
            bool pushed = Sdl.PInvoke.PushEvent(&text) != 0;
            bool received = pump.WaitForEvents(TimeSpan.FromSeconds(30));
            PlatformEventBatch batch = pump.Pump();
            if (timedOut && pushed && received && batch.Events.Length == 1 && batch.GetText(batch.Events[0]).Length == 6)
            {
                handed++;
            }
        }

        return handed;
    }

    [Fact]
    public void WindowEvent_RefreshesSnapshot()
    {
        using var variable = NativeEnvironmentVariable.Set("SDL_VIDEO_DRIVER", null);
        using SdlVideoSession session = StartDummy();
        using SdlPlatformWindow window = session.CreateWindow(NoSurfaceWindow).Value;
        SdlWindow handle = Sdl.PInvoke.GetWindowFromID(window.Id.Value);
        SdlEventPump pump = new(Capacity, TextCapacity, new ScriptedClock(), new LiveWindows(window));
        _ = pump.Pump();

        // The OS resizes the window (a raw SDL_SetWindowSize, as a user dragging its edge would): the getters keep the
        // snapshot until the pump takes the resize event.
        Assert.NotEqual(0, Sdl.PInvoke.SetWindowSize(handle, 500, 400));
        Assert.NotEqual(0, Sdl.PInvoke.SyncWindow(handle));
        Assert.Equal(new WindowSize(320, 200), window.Size);

        PlatformEventBatch batch = pump.Pump();

        Assert.Contains(batch.Events.ToArray(), e => e.Kind == PlatformEventKind.WindowResized && e.Size == new WindowSize(500, 400));
        Assert.Equal(new WindowSize(500, 400), window.Size);
        Assert.Equal(new WindowSize(500, 400), window.PixelSize);
        Assert.Equal(new WindowSize(500, 400), window.ContentSize);
        Assert.Equal(0, pump.RefreshFailureCount);
    }

    // Every SDL window-range type (514 to 538) refreshes the live window it names, the ones the translator ignores
    // included; types outside the range, and a keyboard event that also carries a window id, look up nothing. Below the
    // range the row is 512, not 513: pushing type 513 (0x201, SDL 2's old SYSWM slot) crashes SDL_PushEvent itself
    // (observed on the dummy driver).
    [Theory]
    [InlineData(SdlEventType.WindowShown, true)]
    [InlineData(SdlEventType.WindowHidden, true)]
    [InlineData(SdlEventType.WindowExposed, true)]
    [InlineData(SdlEventType.WindowMoved, true)]
    [InlineData(SdlEventType.WindowResized, true)]
    [InlineData(SdlEventType.WindowPixelSizeChanged, true)]
    [InlineData(SdlEventType.WindowMetalViewResized, true)]
    [InlineData(SdlEventType.WindowMinimized, true)]
    [InlineData(SdlEventType.WindowMaximized, true)]
    [InlineData(SdlEventType.WindowRestored, true)]
    [InlineData(SdlEventType.WindowMouseEnter, true)]
    [InlineData(SdlEventType.WindowMouseLeave, true)]
    [InlineData(SdlEventType.WindowFocusGained, true)]
    [InlineData(SdlEventType.WindowFocusLost, true)]
    [InlineData(SdlEventType.WindowCloseRequested, true)]
    [InlineData(SdlEventType.WindowHitTest, true)]
    [InlineData(SdlEventType.WindowIccprofChanged, true)]
    [InlineData(SdlEventType.WindowDisplayChanged, true)]
    [InlineData(SdlEventType.WindowDisplayScaleChanged, true)]
    [InlineData(SdlEventType.WindowSafeAreaChanged, true)]
    [InlineData(SdlEventType.WindowOccluded, true)]
    [InlineData(SdlEventType.WindowEnterFullscreen, true)]
    [InlineData(SdlEventType.WindowLeaveFullscreen, true)]
    [InlineData(SdlEventType.WindowDestroyed, true)]
    [InlineData(SdlEventType.WindowHdrStateChanged, true)]
    [InlineData((SdlEventType)512, false)]
    [InlineData((SdlEventType)539, false)]
    [InlineData(SdlEventType.KeyDown, false)]
    public void WindowRangeEvents_RefreshLiveWindowOnly(SdlEventType type, bool windowRange)
    {
        using var variable = NativeEnvironmentVariable.Set("SDL_VIDEO_DRIVER", null);
        using SdlVideoSession session = StartDummy();
        FakeWindows windows = new();
        FakeWindow live = windows.Add(WindowIdValue);
        SdlEventPump pump = new(Capacity, TextCapacity, new ScriptedClock(), windows);

        // The live window, a window id the lookup doesn't know, and no window at all (id 0).
        Push(new SdlEvent { window = new SdlWindowEvent { type = type, windowID = WindowIdValue } });
        Push(new SdlEvent { window = new SdlWindowEvent { type = type, windowID = WindowIdValue + 1 } });
        Push(new SdlEvent { window = new SdlWindowEvent { type = type, windowID = 0 } });
        _ = pump.Pump();

        uint[] expected = windowRange ? [WindowIdValue, WindowIdValue + 1] : [];
        Assert.Equal(expected, windows.Lookups.ToArray());
        Assert.Equal(windowRange ? 1 : 0, live.Refreshes);
        Assert.Equal(0, pump.RefreshFailureCount);
        Assert.False(pump.DisplaysChanged);
    }

    // Every SDL display-range type (337 to 344), the ones the translator ignores included, until acknowledged; the
    // types just outside the range don't.
    [Theory]
    [InlineData(SdlEventType.DisplayOrientation, true)]
    [InlineData(SdlEventType.DisplayAdded, true)]
    [InlineData(SdlEventType.DisplayRemoved, true)]
    [InlineData(SdlEventType.DisplayMoved, true)]
    [InlineData(SdlEventType.DisplayDesktopModeChanged, true)]
    [InlineData(SdlEventType.DisplayCurrentModeChanged, true)]
    [InlineData(SdlEventType.DisplayContentScaleChanged, true)]
    [InlineData(SdlEventType.DisplayUsableBoundsChanged, true)]
    [InlineData((SdlEventType)336, false)]
    [InlineData((SdlEventType)345, false)]
    public void DisplayRangeEvents_SetDisplaysChanged(SdlEventType type, bool displayRange)
    {
        using var variable = NativeEnvironmentVariable.Set("SDL_VIDEO_DRIVER", null);
        using SdlVideoSession session = StartDummy();
        FakeWindows windows = new();
        SdlEventPump pump = new(Capacity, TextCapacity, new ScriptedClock(), windows);
        Assert.False(pump.DisplaysChanged);

        Push(new SdlEvent { display = new SdlDisplayEvent { type = type, displayID = DisplayIdValue } });
        _ = pump.Pump();
        Assert.Equal(displayRange, pump.DisplaysChanged);

        // It stays set over a pump with no display event, until acknowledged; the next display event sets it again.
        Push(Motion(1f));
        _ = pump.Pump();
        Assert.Equal(displayRange, pump.DisplaysChanged);
        pump.AcknowledgeDisplaysChanged();
        Assert.False(pump.DisplaysChanged);
        Push(new SdlEvent { display = new SdlDisplayEvent { type = type, displayID = DisplayIdValue } });
        _ = pump.Pump();
        Assert.Equal(displayRange, pump.DisplaysChanged);
        Assert.Empty(windows.Lookups);
    }

    [Fact]
    public void WaitForEvents_HeldEvents_HookedAtPumpNotAtWait()
    {
        using var variable = NativeEnvironmentVariable.Set("SDL_VIDEO_DRIVER", null);
        using SdlVideoSession session = StartDummy();
        FakeWindows windows = new();
        FakeWindow live = windows.Add(WindowIdValue);
        SdlEventPump pump = new(Capacity, TextCapacity, new ScriptedClock(), windows);

        // A held window event refreshes at the pump that hands it out, so the snapshot changes only in the pump.
        Push(new SdlEvent { window = new SdlWindowEvent { type = SdlEventType.WindowEnterFullscreen, windowID = WindowIdValue } });
        Assert.True(pump.WaitForEvents(TimeSpan.FromSeconds(30)));
        Assert.Equal(0, live.Refreshes);
        _ = pump.Pump();
        Assert.Equal(1, live.Refreshes);

        Push(new SdlEvent { display = new SdlDisplayEvent { type = SdlEventType.DisplayMoved, displayID = DisplayIdValue } });
        Assert.True(pump.WaitForEvents(TimeSpan.FromSeconds(30)));
        Assert.False(pump.DisplaysChanged);
        _ = pump.Pump();
        Assert.True(pump.DisplaysChanged);
        Assert.Equal(1, live.Refreshes);
    }

    // The OS takes a window created fullscreen out of fullscreen by itself (a raw SDL_SetWindowFullscreen, as a
    // window-manager shortcut would). The pump's refresh sees it windowed and drops the size kept for SetMode, so it
    // isn't applied now, nor by the next fullscreen round trip. Created at a content scale of 2, so SDL's own windowed
    // size (the 640 by 400 estimate) differs from the 320 by 200 the window would otherwise be resized to.
    [Fact]
    public void WindowedRefresh_ClearsPendingSize()
    {
        using var variable = NativeEnvironmentVariable.Set("SDL_VIDEO_DRIVER", null);
        using SdlVideoSession session = StartDummy();
        WindowOptions options = NoSurfaceWindow with { Mode = WindowMode.BorderlessFullscreen };
        using SdlPlatformWindow window = SdlPlatformWindow.Create(options, session.VideoDriver, session.Headless, estimateScale: 2f).Value;
        SdlWindow handle = Sdl.PInvoke.GetWindowFromID(window.Id.Value);
        SdlEventPump pump = new(Capacity, TextCapacity, new ScriptedClock(), new LiveWindows(window));
        _ = pump.Pump();
        Assert.Equal(WindowMode.BorderlessFullscreen, window.Mode);

        Assert.NotEqual(0, Sdl.PInvoke.SetWindowFullscreen(handle, 0));
        Assert.NotEqual(0, Sdl.PInvoke.SyncWindow(handle));
        WindowSize osWindowed = NativeSize(handle);
        Assert.NotEqual(NoSurfaceWindow.ContentSize, osWindowed);
        Push(new SdlEvent { window = new SdlWindowEvent { type = SdlEventType.WindowLeaveFullscreen, windowID = window.Id.Value } });
        _ = pump.Pump();

        Assert.Equal(WindowMode.Windowed, window.Mode);
        Assert.Equal(osWindowed, NativeSize(handle));
        Assert.True(window.SetMode(WindowMode.BorderlessFullscreen).IsSuccess);
        Assert.True(window.SetMode(WindowMode.Windowed).IsSuccess);
        Assert.Equal(osWindowed, NativeSize(handle));
    }

    // A refresh that sees the window still fullscreen keeps the pending size, so SetMode's return applies it.
    [Fact]
    public void FullscreenRefresh_KeepsPendingSize()
    {
        using var variable = NativeEnvironmentVariable.Set("SDL_VIDEO_DRIVER", null);
        using SdlVideoSession session = StartDummy();
        WindowOptions options = NoSurfaceWindow with { Mode = WindowMode.BorderlessFullscreen };
        using SdlPlatformWindow window = SdlPlatformWindow.Create(options, session.VideoDriver, session.Headless, estimateScale: 2f).Value;
        SdlWindow handle = Sdl.PInvoke.GetWindowFromID(window.Id.Value);
        SdlEventPump pump = new(Capacity, TextCapacity, new ScriptedClock(), new LiveWindows(window));
        Push(new SdlEvent { window = new SdlWindowEvent { type = SdlEventType.WindowEnterFullscreen, windowID = window.Id.Value } });
        _ = pump.Pump();
        Assert.Equal(WindowMode.BorderlessFullscreen, window.Mode);

        Assert.True(window.SetMode(WindowMode.Windowed).IsSuccess);

        Assert.Equal(NoSurfaceWindow.ContentSize, NativeSize(handle));
    }

    [Fact]
    public void RefreshFailure_KeepsEventAndSnapshot()
    {
        using var variable = NativeEnvironmentVariable.Set("SDL_VIDEO_DRIVER", null);
        using SdlVideoSession session = StartDummy();
        FakeWindows windows = new();
        FakeWindow failing = windows.Add(WindowIdValue);
        FakeWindow working = windows.Add(WindowIdValue + 1);
        failing.Failures.Enqueue(new EngineError(ErrorKind.Native, "SDL_GetWindowSize failed: first"));
        failing.Failures.Enqueue(new EngineError(ErrorKind.Native, "SDL_GetWindowSize failed: second"));
        SdlEventPump pump = new(Capacity, TextCapacity, new ScriptedClock(), windows);
        Push(new SdlEvent { window = new SdlWindowEvent { type = SdlEventType.WindowResized, windowID = WindowIdValue, data1 = 640, data2 = 480 } });
        Push(new SdlEvent { window = new SdlWindowEvent { type = SdlEventType.WindowShown, windowID = WindowIdValue + 1 } });
        Push(new SdlEvent { window = new SdlWindowEvent { type = SdlEventType.WindowMoved, windowID = WindowIdValue } });

        PlatformEventBatch batch = pump.Pump();

        // Every event is kept, the next window still refreshes, and each failure is counted with its own error.
        PlatformEventKind[] kinds = [PlatformEventKind.WindowResized, PlatformEventKind.WindowShown, PlatformEventKind.WindowMoved];
        Assert.Equal(kinds, batch.Events.ToArray().Select(e => e.Kind).ToArray());
        Assert.Equal(new WindowSize(640, 480), batch.Events[0].Size);
        Assert.Equal(2, failing.Refreshes);
        Assert.Equal(0, failing.Snapshot);
        Assert.Equal(1, working.Refreshes);
        Assert.Equal(1, working.Snapshot);
        Assert.Equal(2, pump.RefreshFailureCount);
        Assert.Equal(new EngineError(ErrorKind.Native, "SDL_GetWindowSize failed: second"), pump.LastRefreshError);

        // A later refresh that succeeds counts nothing and keeps the last error.
        Push(new SdlEvent { window = new SdlWindowEvent { type = SdlEventType.WindowExposed, windowID = WindowIdValue } });
        _ = pump.Pump();
        Assert.Equal(1, failing.Snapshot);
        Assert.Equal(2, pump.RefreshFailureCount);
        Assert.Equal("SDL_GetWindowSize failed: second", pump.LastRefreshError.Message);
    }

    [Fact]
    public void RefreshFailure_NoneYet_DefaultError()
    {
        SdlEventPump pump = new(Capacity, TextCapacity, new ScriptedClock(), _noWindows);

        Assert.Equal(0, pump.RefreshFailureCount);
        Assert.Equal(default, pump.LastRefreshError);
        Assert.False(pump.DisplaysChanged);
    }

    private static WindowSize NativeSize(SdlWindow handle)
    {
        int width, height;

        // Sound: both ints are locals of this frame, which outlives the call.
        Assert.NotEqual(0, Sdl.PInvoke.GetWindowSize(handle, &width, &height));
        return new WindowSize(width, height);
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

    // The text's UTF-8 and a NUL, on the pinned heap.
    private static byte[] PinnedText(string text)
    {
        byte[] pinned = GC.AllocateArray<byte>(Encoding.UTF8.GetByteCount(text) + 1, pinned: true);
        _ = Encoding.UTF8.GetBytes(text, pinned);
        return pinned;
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

    // The platform's lookup over real windows: a window is live until disposed.
    private sealed class LiveWindows(params SdlPlatformWindow[] windows) : SdlEventPump.IWindowLookup
    {
        public SdlPlatformWindow.ISnapshotRefresh? Find(WindowId window)
        {
            foreach (SdlPlatformWindow candidate in windows)
            {
                if (!candidate.IsDisposed && candidate.Id == window)
                {
                    return candidate;
                }
            }

            return null;
        }
    }

    // A lookup over fake windows that records every id it is asked for.
    private sealed class FakeWindows : SdlEventPump.IWindowLookup
    {
        private readonly Dictionary<uint, FakeWindow> _windows = new();

        public List<uint> Lookups { get; } = [];

        public FakeWindow Add(uint id)
        {
            FakeWindow window = new();
            _windows.Add(id, window);
            return window;
        }

        public SdlPlatformWindow.ISnapshotRefresh? Find(WindowId window)
        {
            Lookups.Add(window.Value);
            return _windows.GetValueOrDefault(window.Value);
        }
    }

    // Counts refreshes, fails the next ones with the queued errors, and counts its "snapshot" up on each success only.
    private sealed class FakeWindow : SdlPlatformWindow.ISnapshotRefresh
    {
        public Queue<EngineError> Failures { get; } = new();

        public int Refreshes { get; private set; }

        public int Snapshot { get; private set; }

        public Result RefreshSnapshot()
        {
            Refreshes++;
            if (Failures.TryDequeue(out EngineError error))
            {
                return error;
            }

            Snapshot++;
            return Result.Success;
        }
    }

    // Returns 1, 2, 3, ... and counts the reads, so a row can tell which event took which timestamp.
    private sealed class ScriptedClock : TimeProvider
    {
        public int Reads { get; private set; }

        public override long TimestampFrequency => 1000;

        public override long GetTimestamp() => ++Reads;
    }
}
