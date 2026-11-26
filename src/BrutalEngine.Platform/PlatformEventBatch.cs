using System;
using System.Globalization;

namespace BrutalEngine.Platform;

/// <summary>
/// The events of one pump, in the order the OS delivered them, and the UTF-8 text of its
/// <see cref="PlatformEventKind.TextInput"/> events.
/// </summary>
/// <remarks>
/// <para>
/// A view of a <see cref="PlatformEventBuffer"/>, taken with <see cref="PlatformEventBuffer.AsBatch"/>. The batch,
/// its <see cref="Events"/> and the text <see cref="GetText"/> returns are valid until the buffer's next
/// <see cref="PlatformEventBuffer.Clear"/> (the next pump); reading them after that is a bug. Being a
/// <see langword="ref"/> struct keeps a batch out of fields. <c>default(PlatformEventBatch)</c> is an empty batch.
/// </para>
/// <para>
/// No member allocates, except the ones that throw. Use it on the thread that pumps, like its buffer.
/// </para>
/// </remarks>
public readonly ref struct PlatformEventBatch
{
    private readonly ReadOnlySpan<PlatformEvent> _events;
    private readonly ReadOnlySpan<byte> _text;

    internal PlatformEventBatch(ReadOnlySpan<PlatformEvent> events, ReadOnlySpan<byte> text, bool hasDeferredEvents)
    {
        _events = events;
        _text = text;
        HasDeferredEvents = hasDeferredEvents;
    }

    /// <summary>Gets the pump's events, in the order the OS delivered them.</summary>
    /// <value>The events; empty when the pump found none.</value>
    public ReadOnlySpan<PlatformEvent> Events => _events;

    /// <summary>
    /// Gets whether the pump stopped with events still queued in the OS because the buffer was full. They arrive,
    /// in order, with the next pump.
    /// </summary>
    /// <value><see langword="true"/> when more events were waiting.</value>
    public bool HasDeferredEvents { get; }

    /// <summary>Gets the UTF-8 text of one of this batch's <see cref="PlatformEventKind.TextInput"/> events.</summary>
    /// <param name="textEvent">A <see cref="PlatformEventKind.TextInput"/> event from <see cref="Events"/>.</param>
    /// <returns>
    /// The text's bytes, without a NUL terminator, valid until the buffer's next
    /// <see cref="PlatformEventBuffer.Clear"/>. Empty when the event's text was empty or cut to nothing.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="textEvent"/> is not a <see cref="PlatformEventKind.TextInput"/> event.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="textEvent"/>'s text lies outside this batch's text, so it came from another buffer or an
    /// earlier pump.
    /// </exception>
    public ReadOnlySpan<byte> GetText(in PlatformEvent textEvent)
    {
        if (textEvent.Kind != PlatformEventKind.TextInput)
        {
            throw new InvalidOperationException(string.Create(
                CultureInfo.InvariantCulture, $"A {textEvent.Kind} event has no text: only TextInput events do."));
        }

        TextEventData text = textEvent.Text;
        if (text.Offset < 0 || text.Length < 0 || text.Length > _text.Length - text.Offset)
        {
            throw new ArgumentException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The event's text (offset {text.Offset}, length {text.Length}) lies outside this batch's "
                    + $"{_text.Length} bytes of text: it isn't one of this batch's events."),
                nameof(textEvent));
        }

        return _text.Slice(text.Offset, text.Length);
    }
}
