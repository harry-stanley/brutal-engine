using System;
using System.Globalization;
using System.Text;

namespace BrutalEngine.Platform;

/// <summary>A key as the current keyboard layout names it, rather than where it sits on the keyboard.</summary>
/// <remarks>
/// <para>
/// The encoding is SDL 3's <c>SDL_Keycode</c>, documented here as the engine's own. A key that produces a character
/// has that character's Unicode code point as its value: a key that types <c>'a'</c> is <c>97</c>, whichever physical
/// key it is on the current layout. Every other key is its <see cref="Scancode"/> with bit 30 (<c>0x40000000</c>)
/// set, as <see cref="FromScancode"/> builds it: F1 is <c>1073741882</c>.
/// </para>
/// <para>
/// Code points include control characters: SDL reports Return as <c>13</c>, Escape as <c>27</c> and Backspace as
/// <c>8</c>, and its unknown key (<c>default(Keycode)</c>) as <c>0</c>, so all of these are characters by
/// <see cref="IsCharacter"/>. Text typed by the user arrives as <c>TextInput</c> events, not as key codes; use a
/// <see cref="Keycode"/> for shortcuts and key bindings that follow the layout, and a <see cref="Scancode"/> for
/// bindings that follow key position.
/// </para>
/// <para>
/// Any <see langword="uint"/> is accepted, because a backend can report keys that this version doesn't know.
/// Equality compares <see cref="Value"/>. Immutable and safe to use from any thread. No member allocates, except
/// <c>ToString</c>.
/// </para>
/// </remarks>
public readonly record struct Keycode
{
    // SDL_KEYCODE_SCANCODE_MASK (Brutal.Sdl/Generated/Sdl.Constants.gen.cs:788).
    private const uint ScancodeMask = 0x40000000;

    /// <summary>Creates a key code from its raw value.</summary>
    /// <param name="value">The value in SDL 3's encoding. Every value is accepted.</param>
    public Keycode(uint value)
    {
        Value = value;
    }

    /// <summary>Gets the raw value, in SDL 3's encoding.</summary>
    public uint Value { get; }

    /// <summary>
    /// Gets whether <see cref="Value"/> is a character's code point: bit 30 is clear and the value is a Unicode scalar
    /// value (at most <c>0x10FFFF</c> and not a surrogate).
    /// </summary>
    /// <remarks>Control characters, such as Return (<c>13</c>), and <c>0</c> count as characters.</remarks>
    public bool IsCharacter => Rune.IsValid(Value);

    /// <summary>Gets the character the key produces.</summary>
    /// <exception cref="InvalidOperationException"><see cref="IsCharacter"/> is <see langword="false"/>.</exception>
    public Rune Character => IsCharacter
        ? new Rune(Value)
        : throw new InvalidOperationException(
            string.Create(CultureInfo.InvariantCulture, $"Key code {Value} is not a character; check IsCharacter first."));

    /// <summary>Returns the key code of a key that produces no character: the scancode with bit 30 set.</summary>
    /// <param name="scancode">The physical key. Any value is accepted, including ones without a member.</param>
    /// <returns>
    /// A key code whose <see cref="Value"/> is <paramref name="scancode"/> OR <c>0x40000000</c>, as SDL 3's
    /// <c>SDL_SCANCODE_TO_KEYCODE</c> builds it. <see cref="IsCharacter"/> is <see langword="false"/> for it.
    /// </returns>
    public static Keycode FromScancode(Scancode scancode) => new((uint)scancode | ScancodeMask);

    /// <summary>Returns the raw value in the record's usual form, for example <c>Keycode { Value = 97 }</c>.</summary>
    /// <returns>
    /// A diagnostic string, formatted with the invariant culture. It never throws, even when the key isn't a
    /// character. Allocates.
    /// </returns>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"Keycode {{ Value = {Value} }}");
}
