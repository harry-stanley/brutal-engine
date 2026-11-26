namespace BrutalEngine.Platform;

/// <summary>Identifies a display (monitor) for as long as it stays connected.</summary>
/// <param name="Value">The backend's id for the display. <c>0</c> is invalid.</param>
/// <remarks>
/// <para>
/// <c>default(DisplayId)</c> (<see cref="Value"/> <c>0</c>) is invalid: it names no display. Every connected
/// display has a non-zero id. An id can outlive its display (it was unplugged), so calls that take one report a
/// display that no longer exists as an expected failure, not as misuse.
/// </para>
/// <para>
/// Equality compares <see cref="Value"/>. Immutable and safe to use from any thread. No member allocates, except
/// <c>ToString</c>.
/// </para>
/// </remarks>
public readonly record struct DisplayId(uint Value);
