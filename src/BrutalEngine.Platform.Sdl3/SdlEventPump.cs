using System;
using System.Runtime.InteropServices;
using System.Text.Unicode;
using System.Threading;
using Brutal.SdlApi;
using BrutalEngine.Core;

namespace BrutalEngine.Platform.Sdl3;

/// <summary>
/// Fills the platform's <see cref="PlatformEventBuffer"/> from SDL's event queue, once per frame, and waits for events
/// when the host has nothing to draw (docs/design/platform.md section 2, "One buffer per pump").
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Pump"/> clears the buffer, lets SDL collect OS events with <c>SDL_PumpEvents</c>, then takes one event
/// at a time with <c>SDL_PeepEvents(SDL_GETEVENT)</c> over SDL's whole type range and translates it with
/// <see cref="SdlEventTranslator"/>, while <see cref="PlatformEventBuffer.HasRoom"/> holds. Events come out in SDL's
/// queue order. When the buffer fills up, the rest stay in SDL's queue, in order, for the next pump, and the pump is
/// recorded as deferred (<see cref="PlatformEventBuffer.MarkDeferred"/>) only if SDL still holds an event.
/// </para>
/// <para>
/// <b>Waiting:</b> <see cref="WaitForEvents"/> keeps the event it receives aside, raw, with a copy of its text in the
/// pump's own storage, because SDL releases its event text at later event calls. The next <see cref="Pump"/>
/// translates it first, into the cleared buffer, so it is cut and counted
/// (<see cref="PlatformEventBuffer.TruncatedTextCount"/>) or ignored exactly as a pumped event would be. The batch the
/// host is reading isn't touched by the wait.
/// </para>
/// <para>
/// <b>SDL's poll sentinel:</b> <c>SDL_PollEvent</c> and <c>SDL_WaitEventTimeout</c> queue an internal
/// <c>SDL_EVENT_POLL_SENTINEL</c> behind the events already queued, and <c>SDL_PollEvent</c> stops at it. So the pump
/// never polls: <c>SDL_PeepEvents</c> skips and removes a sentinel it reaches, and the deferral check asks
/// <c>SDL_PeepEvents(SDL_PEEKEVENT)</c>, which also skips it, rather than <c>SDL_HasEvents</c>, which counts it. A
/// sentinel left anywhere in the queue therefore neither delays an event nor reports a deferral.
/// </para>
/// <para>
/// <b>Snapshots and displays:</b> after translating an event, taken from SDL or held by a wait, the pump refreshes the
/// snapshot of the live window a window-range event names (<see cref="IWindowLookup"/>), for every window type
/// including the ones the translator ignores (entering and leaving fullscreen change the mode), and sets
/// <see cref="DisplaysChanged"/> for every display-range event. The pump doesn't rebuild the display list, which
/// allocates: the platform's <c>Displays</c> getter rebuilds it when <see cref="DisplaysChanged"/> is set, then calls
/// <see cref="AcknowledgeDisplaysChanged"/>. A failed refresh keeps the event and the window's previous snapshot and is
/// counted (<see cref="RefreshFailureCount"/>, <see cref="LastRefreshError"/>).
/// </para>
/// <para>
/// <b>Timestamps:</b> the clock's <see cref="TimeProvider.GetTimestamp"/>, read once for each event taken from SDL,
/// whether the translator keeps it or ignores it; a waited-for event keeps the reading taken when the wait received
/// it. SDL's own event time is never used.
/// </para>
/// <para>
/// <b>Threads:</b> the thread that started SDL video, which owns the buffer; the platform checks it. Not thread-safe.
/// <b>Allocation:</b> the constructor allocates the buffer and the held text's storage; nothing else allocates, except
/// the message of a failure. <b>Ownership:</b> the pump owns its buffer; the platform owns the pump and the clock.
/// </para>
/// </remarks>
internal sealed class SdlEventPump
{
    // SDL's whole event-type range, so no type SDL queues is left behind.
    private const uint FirstType = (uint)SdlEventType.First;
    private const uint LastType = (uint)SdlEventType.Last;

    // SDL's window and display event ranges, which the hooks act on.
    private const uint WindowFirstType = (uint)SdlEventType.WindowFirst;
    private const uint WindowLastType = (uint)SdlEventType.WindowLast;
    private const uint DisplayFirstType = (uint)SdlEventType.DisplayFirst;
    private const uint DisplayLastType = (uint)SdlEventType.DisplayLast;

    // The longest timeout SDL_WaitEventTimeout takes: int.MaxValue milliseconds.
    private const long MaxTimeoutTicks = int.MaxValue * TimeSpan.TicksPerMillisecond;

    // The held text keeps up to 4 bytes (one code point) more than the buffer's text capacity, plus a NUL.
    private const int HeldTextExtraBytes = 4;

    private readonly PlatformEventBuffer _buffer;
    private readonly TimeProvider _clock;
    private readonly IWindowLookup _windows;
    private bool _displaysChanged;

    // The event a wait received, raw, until the next pump translates it; its text, if any, points into _heldText.
    private readonly byte[] _heldText;
    private SdlEvent _held;
    private long _heldTimestamp;
    private bool _hasHeld;

    /// <summary>
    /// Creates a pump with its own event buffer, a clock for the event timestamps and the platform's windows for the
    /// snapshot refreshes.
    /// </summary>
    /// <param name="eventCapacity">How many events one pump can hold: 64 to 65536.</param>
    /// <param name="textCapacityBytes">How many bytes of UTF-8 text one pump can hold: 2048 to 1048576.</param>
    /// <param name="clock">The platform clock, read once for each event taken from SDL.</param>
    /// <param name="windows">The platform's live windows, looked up by the window events' ids.</param>
    /// <remarks>
    /// Allocates the buffer and the held text's storage (<paramref name="textCapacityBytes"/> plus 5 bytes, pinned),
    /// once. The pump creates the buffer so the held text's storage always matches its text capacity.
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="clock"/> or <paramref name="windows"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">A capacity is outside its range.</exception>
    public SdlEventPump(int eventCapacity, int textCapacityBytes, TimeProvider clock, IWindowLookup windows)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(windows);
        _buffer = new PlatformEventBuffer(eventCapacity, textCapacityBytes);
        _clock = clock;
        _windows = windows;

        // Pinned, so the held event can point SDL's text field at it until the next pump reads it.
        _heldText = GC.AllocateArray<byte>(textCapacityBytes + HeldTextExtraBytes + 1, pinned: true);
    }

    /// <summary>
    /// Finds the platform's live windows by id, so the pump can refresh a window's snapshot from its events. The
    /// platform implements it over its window list; tests use fakes.
    /// </summary>
    internal interface IWindowLookup
    {
        /// <summary>Gets the live window with an id.</summary>
        /// <param name="window">The id an SDL window event names; never <c>0</c>.</param>
        /// <returns>
        /// The window, or <see langword="null"/> if no live window has that id (not this platform's, or disposed).
        /// </returns>
        /// <remarks>
        /// Called on the pumping thread, once for each window-range event that names a window. Must not allocate.
        /// </remarks>
        public SdlPlatformWindow.ISnapshotRefresh? Find(WindowId window);
    }

    /// <summary>Gets the buffer each pump clears and fills, which the host reads through its batch.</summary>
    /// <value>The same buffer for the pump's lifetime.</value>
    public PlatformEventBuffer Buffer => _buffer;

    /// <summary>Gets whether a display event was pumped since the last <see cref="AcknowledgeDisplaysChanged"/>.</summary>
    /// <value>
    /// <see langword="true"/> once a pump has taken any display-range event (added, removed, moved, orientation, mode,
    /// content scale or usable bounds changed), until acknowledged; the platform then rebuilds its display list.
    /// </value>
    public bool DisplaysChanged => _displaysChanged;

    /// <summary>Gets how many window snapshot refreshes failed over the pump's lifetime.</summary>
    /// <value>0 until a refresh fails; each failed refresh adds one.</value>
    public long RefreshFailureCount { get; private set; }

    /// <summary>Gets the error of the most recent failed snapshot refresh.</summary>
    /// <value><see langword="default"/> until a refresh fails; later successful refreshes keep it.</value>
    public EngineError LastRefreshError { get; private set; }

    /// <summary>Clears <see cref="DisplaysChanged"/>, after the platform has rebuilt its display list.</summary>
    /// <remarks>A display event pumped afterwards sets it again. Allocates nothing.</remarks>
    public void AcknowledgeDisplaysChanged() => _displaysChanged = false;

    /// <summary>
    /// Replaces the buffer's contents with the event <see cref="WaitForEvents"/> kept aside, if any, then the events
    /// SDL has queued, in order, until the buffer is full or SDL's queue is empty.
    /// </summary>
    /// <returns>
    /// The buffer's new batch, valid until the next <see cref="Pump"/>. Its
    /// <see cref="PlatformEventBatch.HasDeferredEvents"/> is <see langword="true"/> when the buffer filled up while
    /// SDL still held an event, which the next pump returns first.
    /// </returns>
    /// <remarks>
    /// An event the translator ignores is removed from SDL's queue, or from the held slot, and adds nothing, but still
    /// runs the window and display hooks. A window's refresh throws <see cref="ObjectDisposedException"/> or
    /// <see cref="WrongThreadException"/> if the lookup returned a disposed window or the pump runs off the windows'
    /// thread, which are bugs in the caller; the buffer then keeps the events added before. Allocates nothing, except
    /// the message when it throws.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// <c>SDL_PeepEvents</c> failed, which it does only when SDL's event queue isn't running (SDL video was stopped):
    /// the message names the call and carries SDL's error text. The buffer keeps the events added before the failure.
    /// </exception>
    public unsafe PlatformEventBatch Pump()
    {
        _buffer.Clear();
        SdlEventTranslator.SdlScaleSource scales = default;
        if (_hasHeld)
        {
            // First into the cleared buffer, so it has room and its text gets the whole text capacity.
            _hasHeld = false;
            Take(_held, _heldTimestamp, ref scales);
        }

        Sdl.PInvoke.PumpEvents();
        SdlEvent sdlEvent;
        while (_buffer.HasRoom)
        {
            // Sound: sdlEvent is a local of this frame, which outlives the call, and SDL writes at most one event.
            if (Peep(&sdlEvent, SdlEventAction.Getevent) == 0)
            {
                return _buffer.AsBatch();
            }

            Take(sdlEvent, _clock.GetTimestamp(), ref scales);
        }

        // Full: the pump defers only if SDL still holds an event. A peek skips a poll sentinel, as a get does.
        // Sound: as above; a peek copies the event without removing it.
        if (Peep(&sdlEvent, SdlEventAction.Peekevent) != 0)
        {
            _buffer.MarkDeferred();
        }

        return _buffer.AsBatch();
    }

    /// <summary>
    /// Blocks until SDL has an event or the timeout passes, and keeps the event aside for the next <see cref="Pump"/>.
    /// </summary>
    /// <param name="timeout">
    /// How long to wait: <see cref="TimeSpan.Zero"/> only checks, <see cref="Timeout.InfiniteTimeSpan"/> waits without a
    /// limit, and any other value is rounded up to whole milliseconds.
    /// </param>
    /// <returns>
    /// <see langword="true"/> if an event is kept aside: one received now, or one an earlier call kept that no pump has
    /// handed out yet (then this returns at once without calling SDL, so a held event is never replaced).
    /// <see langword="false"/> if the timeout passed.
    /// </returns>
    /// <remarks>
    /// <para>
    /// First checks that SDL's event queue is running with <c>SDL_PeepEvents(SDL_PEEKEVENT)</c>, because
    /// <c>SDL_WaitEventTimeout</c> reports a failure the same way as a timeout. A zero timeout then collects OS events
    /// with <c>SDL_PumpEvents</c> and takes one with <c>SDL_PeepEvents(SDL_GETEVENT)</c>, because
    /// <c>SDL_WaitEventTimeout</c> with 0 polls, and polling stops at SDL's poll sentinel even with events behind it.
    /// Any other timeout blocks in <c>SDL_WaitEventTimeout</c>.
    /// </para>
    /// <para>
    /// The event is kept raw, with the clock's reading as its timestamp. A text event's text is checked whole: a null
    /// text, or one that isn't valid UTF-8, is dropped from the kept event, so the next pump ignores the event as it
    /// would a pumped one. Valid text is copied up to the buffer's text capacity plus one code point, so the next pump
    /// cuts and counts an over-long text as it would a pumped one. The current batch isn't touched. Allocates nothing,
    /// except the message when it throws.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="timeout"/> is negative (other than <see cref="Timeout.InfiniteTimeSpan"/>) or longer than
    /// <see cref="int.MaxValue"/> milliseconds.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// <c>SDL_PeepEvents</c> failed: SDL's event queue isn't running (SDL video was stopped). The message doesn't
    /// carry SDL's error text, because SDL sets none for a failed peek. Nothing is kept.
    /// </exception>
    public unsafe bool WaitForEvents(TimeSpan timeout)
    {
        int milliseconds = ToMilliseconds(timeout);
        if (_hasHeld)
        {
            return true;
        }

        ThrowIfQueueStopped();

        // Sound: sdlEvent is a local of this frame, which outlives each call, and SDL writes at most one event.
        SdlEvent sdlEvent;
        bool received;
        if (milliseconds == 0)
        {
            Sdl.PInvoke.PumpEvents();
            received = Peep(&sdlEvent, SdlEventAction.Getevent) != 0;
        }
        else
        {
            received = Sdl.PInvoke.WaitEventTimeout(&sdlEvent, milliseconds) != 0;
        }

        if (!received)
        {
            return false;
        }

        _held = sdlEvent;
        _heldTimestamp = _clock.GetTimestamp();
        if (sdlEvent.type == SdlEventType.TextInput)
        {
            _held.text.text = HoldText(sdlEvent.text.text);
        }

        _hasHeld = true;
        return true;
    }

    // Translates one event, taken from SDL or held by a wait, then runs the hooks for its type range: a window event
    // refreshes the snapshot of the live window it names, even when the translator ignores its type, and a display
    // event marks the display list for a rebuild.
    private void Take(in SdlEvent sdlEvent, long timestamp, ref SdlEventTranslator.SdlScaleSource scales)
    {
        _ = SdlEventTranslator.TryAdd(sdlEvent, _buffer, timestamp, ref scales);
        uint type = (uint)sdlEvent.type;
        if (type >= WindowFirstType && type <= WindowLastType)
        {
            Refresh(sdlEvent.window.windowID);
        }
        else if (type >= DisplayFirstType && type <= DisplayLastType)
        {
            _displaysChanged = true;
        }
    }

    // Refreshes the live window an event names, if any; a failure keeps the event and the window's previous snapshot.
    private void Refresh(uint windowId)
    {
        if (windowId == 0)
        {
            return;
        }

        SdlPlatformWindow.ISnapshotRefresh? window = _windows.Find(new WindowId(windowId));
        if (window is not null && window.RefreshSnapshot().TryGetError(out EngineError error))
        {
            RefreshFailureCount++;
            LastRefreshError = error;
        }
    }

    /// <summary>
    /// Converts a wait's timeout to the whole milliseconds <c>SDL_WaitEventTimeout</c> takes, rounded up, so a wait
    /// never ends early.
    /// </summary>
    /// <param name="timeout">The timeout <see cref="WaitForEvents"/> was given.</param>
    /// <returns>-1 for <see cref="Timeout.InfiniteTimeSpan"/>; otherwise 0 to <see cref="int.MaxValue"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="timeout"/> is negative (other than <see cref="Timeout.InfiniteTimeSpan"/>) or longer than
    /// <see cref="int.MaxValue"/> milliseconds.
    /// </exception>
    internal static int ToMilliseconds(TimeSpan timeout)
    {
        if (timeout == Timeout.InfiniteTimeSpan)
        {
            return -1;
        }

        long ticks = timeout.Ticks;
        if (ticks < 0 || ticks > MaxTimeoutTicks)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                timeout,
                "The timeout must be from zero to int.MaxValue milliseconds, or Timeout.InfiniteTimeSpan.");
        }

        return (int)((ticks + TimeSpan.TicksPerMillisecond - 1) / TimeSpan.TicksPerMillisecond);
    }

    // Copies a received text event's text into _heldText and returns the copy, NUL-terminated, or null when SDL's text
    // is null or isn't valid UTF-8 (the translator then ignores the event). A text longer than the buffer's text
    // capacity keeps the whole code points that end within 4 bytes past it, so it is still longer than the capacity
    // and the buffer cuts it at the same point as the original and counts it.
    private unsafe byte* HoldText(byte* text)
    {
        if (text is null)
        {
            return null;
        }

        // Sound: SDL's text is a NUL-terminated string that stays valid until the next SDL event call, and the span
        // over it is read and copied before this returns.
        ReadOnlySpan<byte> utf8 = MemoryMarshal.CreateReadOnlySpanFromNullTerminated(text);
        if (!Utf8.IsValid(utf8))
        {
            return null;
        }

        int length = utf8.Length;
        int limit = _heldText.Length - 1;
        if (length > limit)
        {
            // Valid UTF-8, so stepping back over continuation bytes (10xxxxxx) ends the copy on a whole code point.
            length = limit;
            while (length > 0 && (utf8[length] & 0xC0) == 0x80)
            {
                length--;
            }
        }

        utf8[..length].CopyTo(_heldText);
        _heldText[length] = 0;

        // Sound: _heldText is on the pinned heap, so the pointer stays valid after the fixed block, for the pump's
        // lifetime.
        fixed (byte* copy = _heldText)
        {
            return copy;
        }
    }

    // SDL_WaitEventTimeout returns false both on a timeout and when SDL's event queue isn't running, so the wait checks
    // first with a peek, which fails only then. A failed peek leaves SDL's previous error text as it was (observed on
    // the dummy driver), so the message doesn't read SDL_GetError.
    private static unsafe void ThrowIfQueueStopped()
    {
        // Sound: sdlEvent is a local of this frame, which outlives the call, and a peek copies at most one event.
        SdlEvent sdlEvent;
        if (Sdl.PInvoke.PeepEvents(&sdlEvent, 1, SdlEventAction.Peekevent, FirstType, LastType) < 0)
        {
            throw new InvalidOperationException(
                "SDL_PeepEvents failed: SDL's event queue isn't running: wait only while the video session is live.");
        }
    }

    // One event from the front of SDL's queue over the whole type range: 1 if there was one, 0 if not.
    private static unsafe int Peep(SdlEvent* sdlEvent, SdlEventAction action)
    {
        int count = Sdl.PInvoke.PeepEvents(sdlEvent, 1, action, FirstType, LastType);
        if (count < 0)
        {
            throw new InvalidOperationException(
                SdlCheck.Error("SDL_PeepEvents").Message
                + ". SDL's event queue isn't running: pump only while the video session is live.");
        }

        return count;
    }
}
