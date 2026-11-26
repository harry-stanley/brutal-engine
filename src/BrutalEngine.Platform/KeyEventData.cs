namespace BrutalEngine.Platform;

/// <summary>The payload of a <c>KeyDown</c> or <c>KeyUp</c> event: which key, and the modifier state.</summary>
/// <param name="Scancode">The physical key.</param>
/// <param name="Keycode">The key in the keyboard layout that was current when the event happened.</param>
/// <param name="Modifiers">The modifier keys held, and the lock keys on, when the event happened.</param>
/// <param name="IsRepeat">
/// <see langword="true"/> for a key-down that the OS's key repeat generated while the key stayed held;
/// <see langword="false"/> for the first press and for every key-up.
/// </param>
/// <remarks>
/// <para>
/// 12 bytes with no references, so it fits the event's payload at offsets 16 to 27 (docs/design/platform.md,
/// "The event struct").
/// </para>
/// <para>
/// Equality compares every field. Immutable and safe to use from any thread. No member allocates, except
/// <c>ToString</c>.
/// </para>
/// </remarks>
public readonly record struct KeyEventData(Scancode Scancode, Keycode Keycode, KeyModifiers Modifiers, bool IsRepeat);
