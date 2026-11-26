using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Xunit;

namespace BrutalEngine.Platform.Tests;

public sealed class PlatformEventBufferTests
{
    private const int MinEvents = 64;
    private const int MinText = 2048;

    private static readonly WindowId _window = new(7);

    // One event of every kind except TextInput, whose text the buffer stores with it.
    private static readonly PlatformEvent[] _nonTextEvents = MakeNonTextEvents();

    [Theory]
    [InlineData(63, MinText, "eventCapacity")]
    [InlineData(65537, MinText, "eventCapacity")]
    [InlineData(0, MinText, "eventCapacity")]
    [InlineData(-1, MinText, "eventCapacity")]
    [InlineData(int.MinValue, MinText, "eventCapacity")]
    [InlineData(MinEvents, 2047, "textCapacityBytes")]
    [InlineData(MinEvents, 1048577, "textCapacityBytes")]
    [InlineData(MinEvents, -1, "textCapacityBytes")]
    [InlineData(MinEvents, int.MinValue, "textCapacityBytes")]
    public void Constructor_OutOfRange_Throws(int eventCapacity, int textCapacityBytes, string paramName)
    {
        ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => new PlatformEventBuffer(eventCapacity, textCapacityBytes));

        Assert.Equal(paramName, ex.ParamName);
    }

    [Theory]
    [InlineData(64, 2048)]
    [InlineData(65536, 1048576)]
    public void Constructor_InRange_CreatesEmptyBuffer(int eventCapacity, int textCapacityBytes)
    {
        PlatformEventBuffer buffer = new(eventCapacity, textCapacityBytes);

        Assert.Equal(eventCapacity, buffer.Capacity);
        Assert.Equal(0, buffer.Count);
        Assert.True(buffer.HasRoom);
        Assert.True(buffer.AsBatch().Events.IsEmpty);
    }

    [Fact]
    public void Add_KeepsEventsInOrder()
    {
        PlatformEventBuffer buffer = new(MinEvents, MinText);

        foreach (PlatformEvent e in _nonTextEvents)
        {
            buffer.Add(e);
        }

        Assert.Equal(_nonTextEvents.Length, buffer.Count);
        AssertSameEvents(_nonTextEvents, buffer.AsBatch().Events);
    }

    [Fact]
    public void Add_WhenFull_Throws()
    {
        PlatformEventBuffer buffer = new(MinEvents, MinText);
        for (int i = 0; i < MinEvents; i++)
        {
            buffer.Add(Key(i));
        }

        Assert.Throws<InvalidOperationException>(() => buffer.Add(Key(MinEvents)));

        Assert.Equal(MinEvents, buffer.Count);
        Assert.Equal(MinEvents - 1, buffer.AsBatch().Events[^1].Timestamp);
    }

    [Fact]
    public void Add_TextInputEvent_Throws()
    {
        PlatformEventBuffer buffer = new(MinEvents, MinText);
        var text = PlatformEvent.CreateText(Header(PlatformEventKind.TextInput, 1), new TextEventData(0, 0));

        ArgumentException ex = Assert.Throws<ArgumentException>(() => buffer.Add(text));

        Assert.Equal("platformEvent", ex.ParamName);
        Assert.Equal(0, buffer.Count);
    }

    [Fact]
    public void HasRoom_LastEventSlot_FalseOnceFilled()
    {
        PlatformEventBuffer buffer = new(MinEvents, MinText);
        for (int i = 0; i < MinEvents - 1; i++)
        {
            buffer.Add(Key(i));
        }

        bool beforeLast = buffer.HasRoom;
        buffer.Add(Key(MinEvents));

        Assert.True(beforeLast);
        Assert.False(buffer.HasRoom);
    }

    [Fact]
    public void Clear_EmptiesEvents()
    {
        PlatformEventBuffer buffer = new(MinEvents, MinText);
        for (int i = 0; i < MinEvents; i++)
        {
            buffer.Add(Key(i));
        }

        buffer.Clear();
        buffer.Add(Key(100));

        Assert.Equal(1, buffer.Count);
        Assert.True(buffer.HasRoom);
        Assert.Equal(100, buffer.AsBatch().Events[0].Timestamp);
    }

    [Fact]
    public void AsBatch_IsTheBufferWhenTaken()
    {
        PlatformEventBuffer buffer = new(MinEvents, MinText);
        buffer.Add(Key(1));
        PlatformEventBatch earlier = buffer.AsBatch();

        buffer.Add(Key(2));

        Assert.Equal(1, earlier.Events.Length);
        Assert.Equal(2, buffer.AsBatch().Events.Length);
    }

    [Fact]
    public void DefaultBatch_IsEmpty()
    {
        PlatformEventBatch batch = default;

        Assert.True(batch.Events.IsEmpty);
    }

    private static PlatformEvent[] MakeNonTextEvents()
    {
        List<PlatformEvent> events = [];
        PlatformEventKind last = PlatformEventKind.DisplayCurrentModeChanged;
        for (PlatformEventKind kind = PlatformEventKind.QuitRequested; kind <= last; kind++)
        {
            if (kind != PlatformEventKind.TextInput)
            {
                events.Add(MakeEvent(kind, 100 + (int)kind));
            }
        }

        return [.. events];
    }

    private static PlatformEvent MakeEvent(PlatformEventKind kind, long timestamp)
    {
        PlatformEventHeader window = new(kind, _window, timestamp);
        PlatformEventHeader platformWide = new(kind, default, timestamp);
        return kind switch
        {
            PlatformEventKind.QuitRequested => PlatformEvent.Create(platformWide),
            PlatformEventKind.KeyDown or PlatformEventKind.KeyUp => PlatformEvent.CreateKey(
                window, new KeyEventData(Scancode.A, new Keycode(97), KeyModifiers.LeftShift, false)),
            PlatformEventKind.MouseMotion => PlatformEvent.CreateMouseMotion(
                window, new MouseMotionData(1, 2, 3, 4, MouseButtons.Left)),
            PlatformEventKind.MouseButtonDown or PlatformEventKind.MouseButtonUp => PlatformEvent.CreateMouseButton(
                window, new MouseButtonData(MouseButton.Right, 2, 5, 6)),
            PlatformEventKind.MouseWheel => PlatformEvent.CreateMouseWheel(window, new MouseWheelData(1, -1, 7, 8)),
            PlatformEventKind.WindowResized or PlatformEventKind.WindowPixelSizeChanged => PlatformEvent.CreateSize(
                window, new WindowSize(800, 600)),
            PlatformEventKind.WindowDisplayChanged => PlatformEvent.CreateDisplay(window, new DisplayId(3)),
            PlatformEventKind.DisplayAdded or PlatformEventKind.DisplayRemoved
                or PlatformEventKind.DisplayCurrentModeChanged => PlatformEvent.CreateDisplay(
                    platformWide, new DisplayId(4)),
            PlatformEventKind.WindowDisplayScaleChanged => PlatformEvent.CreateScale(window, 1.5f),
            PlatformEventKind.DisplayContentScaleChanged => PlatformEvent.CreateDisplayScale(
                platformWide, new DisplayId(5), 2),
            _ => PlatformEvent.Create(window),
        };
    }

    private static PlatformEventHeader Header(PlatformEventKind kind, long timestamp) => new(kind, _window, timestamp);

    private static PlatformEvent Key(long timestamp) => MakeEvent(PlatformEventKind.KeyDown, timestamp);

    private static void AssertSameEvents(ReadOnlySpan<PlatformEvent> expected, ReadOnlySpan<PlatformEvent> actual)
    {
        Assert.Equal(
            MemoryMarshal.AsBytes(expected).ToArray(), MemoryMarshal.AsBytes(actual).ToArray());
    }
}
