using System;

namespace BrutalEngine.Platform;

/// <summary>The modifier keys held, and the lock keys on, when a key event happened.</summary>
/// <remarks>
/// <para>
/// The bits are SDL 3's <c>SDL_Keymod</c> values, so a backend translates SDL's state with a cast. Each physical key has
/// its own bit; <see cref="Shift"/>, <see cref="Control"/>, <see cref="Alt"/> and <see cref="Gui"/> are both sides
/// together, for tests such as <c>(modifiers &amp; KeyModifiers.Control) != 0</c>, which is true when either Control
/// key is held.
/// </para>
/// <para>
/// The lock bits (<see cref="Num"/>, <see cref="Caps"/>, <see cref="Scroll"/>) report whether the lock is on, as SDL
/// defines them, not whether its key is held. Bits without a member can still arrive from a backend; ignore them. The values are fixed:
/// new members are only ever added, never renumbered.
/// </para>
/// </remarks>
[Flags]
public enum KeyModifiers : ushort
{
    /// <summary>No modifier held and no lock on.</summary>
    None = 0,

    /// <summary>The left Shift key.</summary>
    LeftShift = 1,

    /// <summary>The right Shift key.</summary>
    RightShift = 2,

    /// <summary>The ISO level 5 shift key, on layouts that have one.</summary>
    Level5 = 4,

    /// <summary>The left Control key.</summary>
    LeftControl = 64,

    /// <summary>The right Control key.</summary>
    RightControl = 128,

    /// <summary>The left Alt key.</summary>
    LeftAlt = 256,

    /// <summary>The right Alt key.</summary>
    RightAlt = 512,

    /// <summary>The left GUI key (the Windows or Command key).</summary>
    LeftGui = 1024,

    /// <summary>The right GUI key (the Windows or Command key).</summary>
    RightGui = 2048,

    /// <summary>Num Lock is on.</summary>
    Num = 4096,

    /// <summary>Caps Lock is on.</summary>
    Caps = 8192,

    /// <summary>The mode switch key (SDL's <c>SDL_KMOD_MODE</c>; <see cref="Scancode.Mode"/> is its scancode).</summary>
    Mode = 16384,

    /// <summary>Scroll Lock is on.</summary>
    Scroll = 32768,

    /// <summary>Either Shift key: <see cref="LeftShift"/> | <see cref="RightShift"/>.</summary>
    Shift = LeftShift | RightShift,

    /// <summary>Either Control key: <see cref="LeftControl"/> | <see cref="RightControl"/>.</summary>
    Control = LeftControl | RightControl,

    /// <summary>Either Alt key: <see cref="LeftAlt"/> | <see cref="RightAlt"/>.</summary>
    Alt = LeftAlt | RightAlt,

    /// <summary>Either GUI key: <see cref="LeftGui"/> | <see cref="RightGui"/>.</summary>
    Gui = LeftGui | RightGui,
}
