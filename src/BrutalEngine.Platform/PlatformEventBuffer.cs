using System;
using System.Globalization;

namespace BrutalEngine.Platform;

/// <summary>
/// The events of one pump of the platform's event queue: a fixed number of events and a fixed amount of UTF-8 text,
/// filled by the backend and read through <see cref="AsBatch"/>.
/// </summary>
/// <remarks>
/// <para>
/// The backend clears the buffer at the start of each pump, then translates OS events into it one at a time while
/// <see cref="HasRoom"/> holds. When the buffer fills up with events still queued in the OS, the backend leaves
/// them there, in order, for the next pump: events are deferred, never dropped (docs/design/platform.md, "One
/// buffer per pump").
/// </para>
/// <para>
/// The event array and the text storage are allocated once, by the constructor; no other member allocates, except
/// the ones that throw. Not thread-safe: the buffer is owned by one backend and used on the thread that pumps.
/// </para>
/// </remarks>
public sealed class PlatformEventBuffer
{
    /// <summary>
    /// The text space, in bytes, that <see cref="HasRoom"/> keeps free, so every text event of a pump that stops
    /// while <see cref="HasRoom"/> is false gets at least this much before it is cut.
    /// </summary>
    public const int TextReserveBytes = 1024;

    // The ranges PlatformOptions documents for EventCapacity and TextCapacityBytes.
    private const int MinEventCapacity = 64;
    private const int MaxEventCapacity = 65536;
    private const int MinTextCapacityBytes = 2048;
    private const int MaxTextCapacityBytes = 1048576;

    private readonly PlatformEvent[] _events;
    private readonly byte[] _text;
    private int _count;
    private int _textLength;

    /// <summary>Creates an empty buffer with fixed event and text capacities.</summary>
    /// <param name="eventCapacity">How many events one pump can hold: 64 to 65536.</param>
    /// <param name="textCapacityBytes">How many bytes of UTF-8 text one pump can hold: 2048 to 1048576.</param>
    /// <remarks>Allocates the event array (40 bytes per event) and the text storage, once.</remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="eventCapacity"/> or <paramref name="textCapacityBytes"/> is outside its range.
    /// </exception>
    public PlatformEventBuffer(int eventCapacity, int textCapacityBytes)
    {
        CheckRange(eventCapacity, MinEventCapacity, MaxEventCapacity, nameof(eventCapacity));
        CheckRange(textCapacityBytes, MinTextCapacityBytes, MaxTextCapacityBytes, nameof(textCapacityBytes));
        _events = new PlatformEvent[eventCapacity];
        _text = new byte[textCapacityBytes];
    }

    /// <summary>Gets how many events the buffer holds when full.</summary>
    /// <value>The event capacity the buffer was created with.</value>
    public int Capacity => _events.Length;

    /// <summary>Gets how many events the buffer holds now.</summary>
    /// <value>From 0 to <see cref="Capacity"/>.</value>
    public int Count => _count;

    /// <summary>
    /// Gets whether the backend may add another event: there is room for one more event, and at least
    /// <see cref="TextReserveBytes"/> of text space is free.
    /// </summary>
    /// <value>
    /// <see langword="true"/> while the pump should keep dequeuing; <see langword="false"/> once it should stop and
    /// leave the remaining OS events for the next pump.
    /// </value>
    public bool HasRoom => _count < _events.Length && _text.Length - _textLength >= TextReserveBytes;

    /// <summary>Empties the buffer for the next pump: removes every event and all text.</summary>
    /// <remarks>Ends the lifetime of every batch, span and text the buffer handed out before.</remarks>
    public void Clear()
    {
        _count = 0;
        _textLength = 0;
    }

    /// <summary>Adds an event after the ones already in the buffer.</summary>
    /// <param name="platformEvent">
    /// The event, copied into the buffer. Any kind except <see cref="PlatformEventKind.TextInput"/>.
    /// </param>
    /// <remarks>
    /// The backend checks <see cref="HasRoom"/> before dequeuing an OS event, so a full buffer here is a bug.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// <paramref name="platformEvent"/> is a <see cref="PlatformEventKind.TextInput"/> event, whose text must be stored
    /// in the buffer's text storage with it.
    /// </exception>
    /// <exception cref="InvalidOperationException">The buffer already holds <see cref="Capacity"/> events.</exception>
    public void Add(in PlatformEvent platformEvent)
    {
        if (platformEvent.Kind == PlatformEventKind.TextInput)
        {
            throw new ArgumentException(
                "A TextInput event refers to text in the buffer's text storage, so it can't be added on its own.",
                nameof(platformEvent));
        }

        ThrowIfFull();
        _events[_count++] = platformEvent;
    }

    /// <summary>Gets the events and text the buffer holds now, for the host and input to read.</summary>
    /// <returns>
    /// A view of the buffer as it is now, valid until the next <see cref="Clear"/>. Events added afterwards are in
    /// the next batch taken, not this one.
    /// </returns>
    public PlatformEventBatch AsBatch() =>
        new(_events.AsSpan(0, _count));

    private static void CheckRange(int value, int min, int max, string paramName)
    {
        if (value < min || value > max)
        {
            throw new ArgumentOutOfRangeException(
                paramName,
                value,
                string.Create(CultureInfo.InvariantCulture, $"{paramName} must be from {min} to {max}."));
        }
    }

    private void ThrowIfFull()
    {
        if (_count == _events.Length)
        {
            throw new InvalidOperationException(string.Create(
                CultureInfo.InvariantCulture,
                $"The event buffer is full ({_events.Length} events): stop adding when HasRoom is false."));
        }
    }
}
