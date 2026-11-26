namespace BrutalEngine.Platform;

/// <summary>One mouse button: the button a press or release event is for.</summary>
/// <remarks>
/// <para>
/// The values are SDL 3's button numbers (<c>SDL_BUTTON_LEFT</c> is 1 ... <c>SDL_BUTTON_X2</c> is 5), so a backend
/// translates SDL's button with a cast. <see cref="None"/> is <c>default(MouseButton)</c>, not a real button. The
/// matching bit in <see cref="MouseButtons"/> is <c>1 &lt;&lt; (button - 1)</c>.
/// </para>
/// <para>
/// Backed by a <see langword="byte"/>, so <see cref="MouseButtonData"/> fits the event payload. Buttons beyond
/// <see cref="X2"/> can still arrive from a backend, as values without a member. The values are fixed: new members are
/// only ever added, never renumbered.
/// </para>
/// </remarks>
public enum MouseButton : byte
{
    /// <summary>No button (value 0), the default.</summary>
    None = 0,

    /// <summary>The left (primary) button.</summary>
    Left = 1,

    /// <summary>The middle button.</summary>
    Middle = 2,

    /// <summary>The right (secondary) button.</summary>
    Right = 3,

    /// <summary>The first extra button.</summary>
    X1 = 4,

    /// <summary>The second extra button.</summary>
    X2 = 5,
}
