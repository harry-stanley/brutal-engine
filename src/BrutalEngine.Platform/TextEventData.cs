namespace BrutalEngine.Platform;

/// <summary>
/// The payload of a <c>TextInput</c> event: where its committed UTF-8 text sits in the text storage of the event
/// buffer that holds the event.
/// </summary>
/// <param name="Offset">The text's first byte, counted from the start of the buffer's text storage.</param>
/// <param name="Length">The text's length in bytes.</param>
/// <remarks>
/// <para>
/// The text itself isn't in the event, so events stay unmanaged and a fixed size; read it through the batch the event
/// came from (<c>PlatformEventBatch.GetText</c>). The values only mean something for that buffer, until its next
/// pump. This record doesn't check them; the event buffer that stores the text produces them.
/// </para>
/// <para>
/// 8 bytes with no references, so it fits the event's payload at offsets 16 to 23 (docs/design/platform.md,
/// "The event struct"). Equality compares both fields. Immutable and safe to use from any thread. No member
/// allocates, except <c>ToString</c>.
/// </para>
/// </remarks>
public readonly record struct TextEventData(int Offset, int Length);
