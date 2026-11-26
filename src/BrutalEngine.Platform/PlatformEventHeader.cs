using System.Globalization;

namespace BrutalEngine.Platform;

/// <summary>The part every platform event has: its kind, its window and when it was translated.</summary>
/// <param name="Kind">What the event reports, which decides its payload.</param>
/// <param name="Window">
/// The window the event is for. Default for <see cref="PlatformEventKind.QuitRequested"/> and the <c>Display*</c>
/// kinds; never default for a <c>Window*</c> kind; either for key, text and mouse kinds.
/// </param>
/// <param name="Timestamp">
/// When the backend's pump translated the event: <see cref="System.TimeProvider.GetTimestamp"/> of the platform's
/// clock (<see cref="PlatformOptions.Clock"/>), in that clock's ticks. Not the OS's own event time.
/// </param>
/// <remarks>
/// <para>
/// The record doesn't check its values: the <c>PlatformEvent</c> factories check the kind and window rules
/// (docs/design/platform.md, "The event struct").
/// </para>
/// <para>
/// Equality compares every field. Immutable and safe to use from any thread. No member allocates, except
/// <c>ToString</c>, which formats with the invariant culture.
/// </para>
/// </remarks>
public readonly record struct PlatformEventHeader(PlatformEventKind Kind, WindowId Window, long Timestamp)
{
    /// <summary>
    /// Returns every field in the record's usual form, for example
    /// <c>PlatformEventHeader { Kind = KeyDown, Window = WindowId { Value = 3 }, Timestamp = 120 }</c>. A kind without
    /// a member prints as its number.
    /// </summary>
    /// <returns>A diagnostic string, formatted with the invariant culture whatever the current culture is. Allocates.</returns>
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"PlatformEventHeader {{ Kind = {Kind}, Window = {Window}, Timestamp = {Timestamp} }}");
}
