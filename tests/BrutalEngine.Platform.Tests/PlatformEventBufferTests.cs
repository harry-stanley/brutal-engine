using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using CsCheck;
using Xunit;

namespace BrutalEngine.Platform.Tests;

public sealed class PlatformEventBufferTests
{
    private const int MinEvents = 64;
    private const int MinText = 2048;

    private static readonly WindowId _window = new(7);

    // One event of every kind except TextInput, which only AddText makes.
    private static readonly PlatformEvent[] _nonTextEvents = MakeNonTextEvents();

    // Any event that Add takes: one of every non-text kind, with random headers and payloads.
    private static readonly Gen<PlatformEvent> _genEvent = Gen.OneOf(
        Gen.Select(Gen.UShort[4, 231], Gen.UInt, Gen.Long, Gen.Bool, (code, w, t, repeat) =>
            PlatformEvent.CreateKey(
                new PlatformEventHeader(PlatformEventKind.KeyUp, new WindowId(w), t),
                new KeyEventData((Scancode)code, new Keycode(code), KeyModifiers.None, repeat))),
        Gen.Select(Gen.Float, Gen.Float, Gen.UInt[1, uint.MaxValue], Gen.Long, (x, y, w, t) =>
            PlatformEvent.CreateMouseMotion(
                new PlatformEventHeader(PlatformEventKind.MouseMotion, new WindowId(w), t),
                new MouseMotionData(x, y, -x, -y, MouseButtons.X1))),
        Gen.Select(Gen.Int, Gen.Int, Gen.UInt[1, uint.MaxValue], Gen.Long, (width, height, w, t) =>
            PlatformEvent.CreateSize(
                new PlatformEventHeader(PlatformEventKind.WindowResized, new WindowId(w), t),
                new WindowSize(width, height))),
        Gen.Long.Select(t => PlatformEvent.Create(new PlatformEventHeader(PlatformEventKind.QuitRequested, default, t))));

    // UTF-8 text that over-samples multi-byte sequences: each code point is 1, 2, 3 or 4 bytes with equal weight, so
    // a cut lands inside a sequence about as often as between two. Up to 2,400 bytes, so text often exceeds the
    // 2,048-byte buffer and gets cut.
    private static readonly Gen<byte[]> _genText = Gen.OneOf(
            Gen.Int[0x01, 0x7F], Gen.Int[0x80, 0x7FF], Gen.Int[0x800, 0xD7FF], Gen.Int[0xE000, 0xFFFF],
            Gen.Int[0x10000, 0x10FFFF])
        .Array[0, 600]
        .Select(codePoints =>
        {
            StringBuilder text = new();
            foreach (int codePoint in codePoints)
            {
                text.Append(new Rune(codePoint));
            }

            return Encoding.UTF8.GetBytes(text.ToString());
        });

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
        Assert.Equal(0, buffer.DeferredPumpCount);
        Assert.Equal(0, buffer.TruncatedTextCount);
        Assert.True(buffer.AsBatch().Events.IsEmpty);
        Assert.False(buffer.AsBatch().HasDeferredEvents);
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
    public void HasRoom_ReservesTextBytes()
    {
        // 3000 bytes, not the minimum, so a reserve computed from the capacity (half of it) fails here.
        PlatformEventBuffer buffer = new(MinEvents, 3000);

        buffer.AddText(_window, 1, Ascii(3000 - PlatformEventBuffer.TextReserveBytes));
        bool atReserve = buffer.HasRoom;
        buffer.AddText(_window, 2, Ascii(1));

        Assert.True(atReserve);
        Assert.False(buffer.HasRoom);
        Assert.Equal(1024, PlatformEventBuffer.TextReserveBytes);
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
    public void AddText_StoresTextInOrder()
    {
        PlatformEventBuffer buffer = new(MinEvents, MinText);
        byte[] first = Encoding.UTF8.GetBytes("héllo");
        byte[] second = Encoding.UTF8.GetBytes("€😀");

        buffer.AddText(_window, 10, first);
        buffer.Add(Key(11));
        buffer.AddText(default, 12, second);
        buffer.AddText(_window, 13, []);
        PlatformEventBatch batch = buffer.AsBatch();

        Assert.Equal(4, batch.Events.Length);
        Assert.Equal(new PlatformEventHeader(PlatformEventKind.TextInput, _window, 10), batch.Events[0].Header);
        Assert.Equal(new PlatformEventHeader(PlatformEventKind.TextInput, default, 12), batch.Events[2].Header);
        Assert.Equal(first, batch.GetText(batch.Events[0]).ToArray());
        Assert.Equal(11, batch.Events[1].Timestamp);
        Assert.Equal(second, batch.GetText(batch.Events[2]).ToArray());
        Assert.True(batch.GetText(batch.Events[3]).IsEmpty);
        Assert.Equal(0, buffer.TruncatedTextCount);
    }

    [Theory]
    [InlineData("é", 1)]
    [InlineData("€", 1)]
    [InlineData("€", 2)]
    [InlineData("😀", 1)]
    [InlineData("😀", 2)]
    [InlineData("😀", 3)]
    public void AddText_TooLong_TruncatesAtCodePoint(string sequence, int bytesThatFit)
    {
        PlatformEventBuffer buffer = new(MinEvents, MinText);
        buffer.AddText(_window, 1, Ascii(1000));
        int free = MinText - 1000;
        byte[] text = [.. Ascii(free - bytesThatFit), .. Encoding.UTF8.GetBytes(sequence), .. Ascii(10)];

        buffer.AddText(_window, 2, text);
        PlatformEventBatch batch = buffer.AsBatch();

        Assert.Equal(text[..(free - bytesThatFit)], batch.GetText(batch.Events[1]).ToArray());
        Assert.Equal(1, buffer.TruncatedTextCount);
    }

    [Theory]
    [InlineData("é")]
    [InlineData("€")]
    [InlineData("😀")]
    public void AddText_TooLong_KeepsSequenceThatEndsAtTheLimit(string sequence)
    {
        PlatformEventBuffer buffer = new(MinEvents, MinText);
        byte[] encoded = Encoding.UTF8.GetBytes(sequence);
        byte[] text = [.. Ascii(MinText - encoded.Length), .. encoded, .. encoded];

        buffer.AddText(_window, 1, text);
        PlatformEventBatch batch = buffer.AsBatch();

        Assert.Equal(text[..MinText], batch.GetText(batch.Events[0]).ToArray());
        Assert.Equal(1, buffer.TruncatedTextCount);
    }

    [Fact]
    public void AddText_NothingFits_AddsEmptyTextAndCounts()
    {
        PlatformEventBuffer buffer = new(MinEvents, MinText);
        buffer.AddText(_window, 1, Ascii(MinText - 1));

        buffer.AddText(_window, 2, Encoding.UTF8.GetBytes("€"));
        PlatformEventBatch batch = buffer.AsBatch();

        Assert.Equal(2, batch.Events.Length);
        Assert.True(batch.GetText(batch.Events[1]).IsEmpty);
        Assert.Equal(1, buffer.TruncatedTextCount);
    }

    [Theory]
    [InlineData(new byte[] { 0x80 })]                    // a continuation byte without a lead byte
    [InlineData(new byte[] { 0x61, 0xC3 })]              // a 2-byte sequence cut short
    [InlineData(new byte[] { 0xE2, 0x82 })]              // a 3-byte sequence cut short
    [InlineData(new byte[] { 0xF0, 0x9F, 0x98 })]        // a 4-byte sequence cut short
    [InlineData(new byte[] { 0xC0, 0x80 })]              // an overlong encoding of U+0000
    [InlineData(new byte[] { 0xED, 0xA0, 0x80 })]        // a UTF-16 surrogate, U+D800
    [InlineData(new byte[] { 0xF4, 0x90, 0x80, 0x80 })]  // above U+10FFFF
    [InlineData(new byte[] { 0xFF })]                    // never valid in UTF-8
    public void AddText_InvalidUtf8_Throws(byte[] utf8)
    {
        PlatformEventBuffer buffer = new(MinEvents, MinText);

        ArgumentException ex = Assert.Throws<ArgumentException>(() => buffer.AddText(_window, 1, utf8));

        Assert.Equal("utf8", ex.ParamName);
        Assert.Equal(0, buffer.Count);
        Assert.Equal(MinText, FreeText(buffer));
    }

    [Fact]
    public void AddText_WhenFull_Throws()
    {
        PlatformEventBuffer buffer = new(MinEvents, MinText);
        for (int i = 0; i < MinEvents; i++)
        {
            buffer.Add(Key(i));
        }

        Assert.Throws<InvalidOperationException>(() => buffer.AddText(_window, 1, Ascii(1)));

        Assert.Equal(MinEvents, buffer.Count);
        buffer.Clear();
        Assert.Equal(MinText, FreeText(buffer));
    }

    [Fact]
    public void GetText_NonTextEvent_Throws()
    {
        PlatformEventBuffer buffer = new(MinEvents, MinText);
        buffer.AddText(_window, 1, Ascii(5));
        PlatformEvent[] events = [.. _nonTextEvents, default];

        foreach (PlatformEvent e in events)
        {
            Assert.Throws<InvalidOperationException>(() => buffer.AsBatch().GetText(e));
        }
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    [InlineData(0, 6)]
    [InlineData(5, 1)]
    [InlineData(6, 0)]
    [InlineData(int.MaxValue, 1)]
    [InlineData(1, int.MaxValue)]
    public void GetText_TextOutsideTheBatch_Throws(int offset, int length)
    {
        PlatformEventBuffer buffer = new(MinEvents, MinText);
        buffer.AddText(_window, 1, Ascii(5));
        var foreign = PlatformEvent.CreateText(
            Header(PlatformEventKind.TextInput, 2), new TextEventData(offset, length));

        ArgumentException ex = Assert.Throws<ArgumentException>(() => buffer.AsBatch().GetText(foreign));

        Assert.Equal("textEvent", ex.ParamName);
    }

    [Fact]
    public void MarkDeferred_SetsFlagUntilClear()
    {
        PlatformEventBuffer buffer = new(MinEvents, MinText);
        bool before = buffer.AsBatch().HasDeferredEvents;

        buffer.MarkDeferred();
        buffer.MarkDeferred();
        bool marked = buffer.AsBatch().HasDeferredEvents;
        long countedOnce = buffer.DeferredPumpCount;
        buffer.Clear();
        bool afterClear = buffer.AsBatch().HasDeferredEvents;
        long keptByClear = buffer.DeferredPumpCount;
        buffer.MarkDeferred();

        Assert.False(before);
        Assert.True(marked);
        Assert.Equal(1, countedOnce);
        Assert.False(afterClear);
        Assert.Equal(1, keptByClear);
        Assert.Equal(2, buffer.DeferredPumpCount);
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
    public void Clear_EmptiesEventsAndText_KeepsCounters()
    {
        PlatformEventBuffer buffer = new(MinEvents, MinText);
        buffer.Add(Key(1));
        buffer.AddText(_window, 2, Ascii(MinText + 1));

        buffer.Clear();

        Assert.Equal(0, buffer.Count);
        Assert.True(buffer.AsBatch().Events.IsEmpty);
        Assert.True(buffer.HasRoom);
        Assert.Equal(1, buffer.TruncatedTextCount);
        Assert.Equal(MinText, FreeText(buffer));
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
        var empty = PlatformEvent.CreateText(Header(PlatformEventKind.TextInput, 1), new TextEventData(0, 0));

        Assert.True(batch.Events.IsEmpty);
        Assert.False(batch.HasDeferredEvents);
        Assert.True(batch.GetText(empty).IsEmpty);
    }

    [Fact]
    public void AddsAndClears_MatchReferenceModel()
    {
        var op = Gen.Frequency(
            (8, _genEvent.Select(e => new Op(OpKind.Add, e, default, 0, []))),
            (6, Gen.Select(Gen.UInt, Gen.Long, _genText, (w, t, text) =>
                new Op(OpKind.AddText, default, new WindowId(w), t, text))),
            (1, Gen.Const(new Op(OpKind.Clear, default, default, 0, []))),
            (1, Gen.Const(new Op(OpKind.MarkDeferred, default, default, 0, []))));

        op.Array[0, 200].Sample(ops =>
        {
            PlatformEventBuffer buffer = new(MinEvents, MinText);
            Model model = new();
            foreach (Op step in ops)
            {
                model.Apply(buffer, step);
                model.AssertMatches(buffer);
            }
        });
    }

    [Fact]
    public void FillAndRead_DoesNotAllocate()
    {
        PlatformEventBuffer buffer = new(MinEvents, MinText);
        byte[] shortText = Encoding.UTF8.GetBytes("aé€😀");
        byte[] longText = [.. Ascii(1499), .. Encoding.UTF8.GetBytes("😀")];
        _ = FillAndRead(buffer, shortText, longText, 1);

        // A collection between warm-up and the window rebuilds any cache that only a weak handle keeps.
        GC.Collect();
        long before = GC.GetAllocatedBytesForCurrentThread();

        double checksum = FillAndRead(buffer, shortText, longText, 1000);

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
        Assert.NotEqual(0, checksum);
        Assert.Equal(1001, buffer.TruncatedTextCount);
        Assert.Equal(1001, buffer.DeferredPumpCount);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static double FillAndRead(PlatformEventBuffer buffer, byte[] shortText, byte[] longText, int batches)
    {
        double sum = 0;
        for (int i = 0; i < batches; i++)
        {
            buffer.Clear();
            foreach (PlatformEvent e in _nonTextEvents)
            {
                buffer.Add(e);
            }

            buffer.AddText(_window, i, shortText);
            buffer.AddText(_window, i, longText);
            buffer.AddText(_window, i, longText); // cut to fit
            buffer.MarkDeferred();

            PlatformEventBatch batch = buffer.AsBatch();
            sum += batch.HasDeferredEvents ? 1 : 0;
            foreach (ref readonly PlatformEvent e in batch.Events)
            {
                sum += ReadPayload(batch, e) + e.Timestamp + e.Window.Value;
            }
        }

        return sum;
    }

    private static double ReadPayload(PlatformEventBatch batch, in PlatformEvent e) => e.Kind switch
    {
        PlatformEventKind.KeyDown or PlatformEventKind.KeyUp => (int)e.Key.Scancode,
        PlatformEventKind.TextInput => batch.GetText(e).Length,
        PlatformEventKind.MouseMotion => e.MouseMotion.DeltaX,
        PlatformEventKind.MouseButtonDown or PlatformEventKind.MouseButtonUp => e.MouseButton.X,
        PlatformEventKind.MouseWheel => e.MouseWheel.Y,
        PlatformEventKind.WindowResized or PlatformEventKind.WindowPixelSizeChanged => e.Size.Width,
        PlatformEventKind.WindowDisplayChanged or PlatformEventKind.DisplayAdded or PlatformEventKind.DisplayRemoved
            or PlatformEventKind.DisplayCurrentModeChanged => e.Display.Value,
        PlatformEventKind.WindowDisplayScaleChanged => e.Scale,
        PlatformEventKind.DisplayContentScaleChanged => e.Display.Value + e.Scale,
        _ => (ushort)e.Kind,
    };

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

    private static byte[] Ascii(int length)
    {
        byte[] bytes = new byte[length];
        bytes.AsSpan().Fill((byte)'a');
        return bytes;
    }

    // The free text space, measured by adding ASCII text one byte past it and reading back what was stored.
    private static int FreeText(PlatformEventBuffer buffer)
    {
        long truncated = buffer.TruncatedTextCount;
        buffer.AddText(_window, 0, Ascii(MinText + 1));
        PlatformEventBatch batch = buffer.AsBatch();
        int free = batch.GetText(batch.Events[^1]).Length;
        Assert.Equal(truncated + 1, buffer.TruncatedTextCount);
        return free;
    }

    private static void AssertSameEvents(ReadOnlySpan<PlatformEvent> expected, ReadOnlySpan<PlatformEvent> actual)
    {
        Assert.Equal(
            MemoryMarshal.AsBytes(expected).ToArray(), MemoryMarshal.AsBytes(actual).ToArray());
    }

    private enum OpKind
    {
        Add,
        AddText,
        Clear,
        MarkDeferred,
    }

    private sealed record Op(OpKind Kind, PlatformEvent Event, WindowId Window, long Timestamp, byte[] Text);

    // The rules of docs/design/platform.md, "One buffer per pump", written independently of the buffer: text is cut
    // by decoding it forwards, one code point at a time, and keeping whole code points while they fit.
    private sealed class Model
    {
        private readonly List<(PlatformEvent Event, byte[]? Text)> _events = [];
        private int _textLength;
        private bool _deferred;
        private long _deferredPumps;
        private long _truncated;

        public void Apply(PlatformEventBuffer buffer, Op op)
        {
            switch (op.Kind)
            {
                case OpKind.Add when _events.Count == MinEvents:
                    Assert.Throws<InvalidOperationException>(() => buffer.Add(op.Event));
                    break;
                case OpKind.Add:
                    buffer.Add(op.Event);
                    _events.Add((op.Event, null));
                    break;
                case OpKind.AddText when _events.Count == MinEvents:
                    Assert.Throws<InvalidOperationException>(() => buffer.AddText(op.Window, op.Timestamp, op.Text));
                    break;
                case OpKind.AddText:
                    buffer.AddText(op.Window, op.Timestamp, op.Text);
                    byte[] kept = KeepWholeCodePoints(op.Text, MinText - _textLength);
                    _truncated += kept.Length < op.Text.Length ? 1 : 0;
                    _textLength += kept.Length;
                    PlatformEventHeader header = new(PlatformEventKind.TextInput, op.Window, op.Timestamp);
                    _events.Add((PlatformEvent.CreateText(header, default), kept));
                    break;
                case OpKind.Clear:
                    buffer.Clear();
                    _events.Clear();
                    _textLength = 0;
                    _deferred = false;
                    break;
                default:
                    buffer.MarkDeferred();
                    _deferredPumps += _deferred ? 0 : 1;
                    _deferred = true;
                    break;
            }
        }

        public void AssertMatches(PlatformEventBuffer buffer)
        {
            PlatformEventBatch batch = buffer.AsBatch();
            Assert.Equal(_events.Count, buffer.Count);
            Assert.Equal(_events.Count, batch.Events.Length);
            Assert.Equal(_deferred, batch.HasDeferredEvents);
            Assert.Equal(_deferredPumps, buffer.DeferredPumpCount);
            Assert.Equal(_truncated, buffer.TruncatedTextCount);
            Assert.Equal(
                _events.Count < MinEvents && MinText - _textLength >= PlatformEventBuffer.TextReserveBytes,
                buffer.HasRoom);
            for (int i = 0; i < _events.Count; i++)
            {
                (PlatformEvent expected, byte[]? text) = _events[i];
                PlatformEvent actual = batch.Events[i];
                if (text is null)
                {
                    AssertSameEvents([expected], [actual]);
                }
                else
                {
                    Assert.Equal(expected.Header, actual.Header);
                    Assert.Equal(text, batch.GetText(actual).ToArray());
                }
            }
        }

        private static byte[] KeepWholeCodePoints(byte[] utf8, int free)
        {
            int kept = 0;
            while (kept < utf8.Length)
            {
                Assert.Equal(OperationStatus.Done, Rune.DecodeFromUtf8(utf8.AsSpan(kept), out _, out int size));
                if (kept + size > free)
                {
                    break;
                }

                kept += size;
            }

            return utf8[..kept];
        }
    }
}
