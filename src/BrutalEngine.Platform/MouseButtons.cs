using System;

namespace BrutalEngine.Platform;

/// <summary>A set of mouse buttons: the buttons held during a motion event.</summary>
/// <remarks>
/// <para>
/// The bits are SDL 3's <c>SDL_MouseButtonFlags</c>: the bit for a <see cref="MouseButton"/> is
/// <c>1 &lt;&lt; (button - 1)</c>, so <see cref="Left"/> is 1 and <see cref="X2"/> is 16. A backend translates SDL's
/// state with a cast.
/// </para>
/// <para>
/// Backed by a <see langword="byte"/>, so <c>MouseMotionData</c> fits the event payload. Bits without a member
/// can still arrive from a backend; ignore them. The values are fixed: new members are only ever added, never
/// renumbered.
/// </para>
/// </remarks>
[Flags]
public enum MouseButtons : byte
{
    /// <summary>No button held.</summary>
    None = 0,

    /// <summary>The left (primary) button.</summary>
    Left = 1,

    /// <summary>The middle button.</summary>
    Middle = 2,

    /// <summary>The right (secondary) button.</summary>
    Right = 4,

    /// <summary>The first extra button.</summary>
    X1 = 8,

    /// <summary>The second extra button.</summary>
    X2 = 16,
}
