using System;
using Brutal.SdlApi;

namespace BrutalEngine.Platform.Sdl3;

/// <summary>
/// Fills the platform's <see cref="PlatformEventBuffer"/> from SDL's event queue, once per frame
/// (docs/design/platform.md section 2, "One buffer per pump").
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
/// <b>SDL's poll sentinel:</b> <c>SDL_PollEvent</c> and <c>SDL_WaitEventTimeout</c> queue an internal
/// <c>SDL_EVENT_POLL_SENTINEL</c> behind the events already queued, and <c>SDL_PollEvent</c> stops at it. So the pump
/// never polls: <c>SDL_PeepEvents</c> skips and removes a sentinel it reaches, and the deferral check asks
/// <c>SDL_PeepEvents(SDL_PEEKEVENT)</c>, which also skips it, rather than <c>SDL_HasEvents</c>, which counts it. A
/// sentinel left anywhere in the queue therefore neither delays an event nor reports a deferral.
/// </para>
/// <para>
/// <b>Timestamps:</b> the clock's <see cref="TimeProvider.GetTimestamp"/>, read once for each event taken from SDL,
/// whether the translator keeps it or ignores it. SDL's own event time is never used.
/// </para>
/// <para>
/// <b>Threads:</b> the thread that started SDL video, which owns the buffer; the platform checks it. Not thread-safe.
/// <b>Allocation:</b> the constructor allocates the buffer; nothing else allocates, except the message of a failure.
/// <b>Ownership:</b> the pump owns its buffer; the platform owns the pump and the clock.
/// </para>
/// </remarks>
internal sealed class SdlEventPump
{
    // SDL's whole event-type range, so no type SDL queues is left behind.
    private const uint FirstType = (uint)SdlEventType.First;
    private const uint LastType = (uint)SdlEventType.Last;

    private readonly PlatformEventBuffer _buffer;
    private readonly TimeProvider _clock;

    /// <summary>Creates a pump with its own event buffer, and a clock for the event timestamps.</summary>
    /// <param name="eventCapacity">How many events one pump can hold: 64 to 65536.</param>
    /// <param name="textCapacityBytes">How many bytes of UTF-8 text one pump can hold: 2048 to 1048576.</param>
    /// <param name="clock">The platform clock, read once for each event taken from SDL.</param>
    /// <remarks>
    /// Allocates the buffer, once. The pump creates the buffer so that storage of its own can match the buffer's text
    /// capacity, which the buffer doesn't expose.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="clock"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A capacity is outside its range.</exception>
    public SdlEventPump(int eventCapacity, int textCapacityBytes, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _buffer = new PlatformEventBuffer(eventCapacity, textCapacityBytes);
        _clock = clock;
    }

    /// <summary>Gets the buffer each pump clears and fills, which the host reads through its batch.</summary>
    /// <value>The same buffer for the pump's lifetime.</value>
    public PlatformEventBuffer Buffer => _buffer;

    /// <summary>
    /// Replaces the buffer's contents with the events SDL has queued, in order, until the buffer is full or SDL's
    /// queue is empty.
    /// </summary>
    /// <returns>
    /// The buffer's new batch, valid until the next <see cref="Pump"/>. Its
    /// <see cref="PlatformEventBatch.HasDeferredEvents"/> is <see langword="true"/> when the buffer filled up while
    /// SDL still held an event, which the next pump returns first.
    /// </returns>
    /// <remarks>
    /// An event the translator ignores is removed from SDL's queue and adds nothing. Allocates nothing, except the
    /// message when it throws.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// <c>SDL_PeepEvents</c> failed, which it does only when SDL's event queue isn't running (SDL video was stopped):
    /// the message names the call and carries SDL's error text. The buffer keeps the events added before the failure.
    /// </exception>
    public unsafe PlatformEventBatch Pump()
    {
        _buffer.Clear();
        Sdl.PInvoke.PumpEvents();
        SdlEventTranslator.SdlScaleSource scales = default;
        SdlEvent sdlEvent;
        while (_buffer.HasRoom)
        {
            // Sound: sdlEvent is a local of this frame, which outlives the call, and SDL writes at most one event.
            if (Peep(&sdlEvent, SdlEventAction.Getevent) == 0)
            {
                return _buffer.AsBatch();
            }

            _ = SdlEventTranslator.TryAdd(sdlEvent, _buffer, _clock.GetTimestamp(), ref scales);
        }

        // Full: the pump defers only if SDL still holds an event. A peek skips a poll sentinel, as a get does.
        // Sound: as above; a peek copies the event without removing it.
        if (Peep(&sdlEvent, SdlEventAction.Peekevent) != 0)
        {
            _buffer.MarkDeferred();
        }

        return _buffer.AsBatch();
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
