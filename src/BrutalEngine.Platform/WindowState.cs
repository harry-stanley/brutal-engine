using System;

namespace BrutalEngine.Platform;

/// <summary>The state of a window, as flags: a window can be visible, maximised and focused at once.</summary>
/// <remarks>
/// <c>IWindow.State</c> is a snapshot that the platform's event pump updates. The values are fixed: new flags are
/// added at new bits, never renumbered.
/// </remarks>
[Flags]
public enum WindowState
{
    /// <summary>No flags: hidden, neither minimised nor maximised, without focus.</summary>
    None = 0,

    /// <summary>The window is shown.</summary>
    Visible = 1,

    /// <summary>The window is minimised. Its pixel size can be 0 by 0, so renderers skip frames while it is set.</summary>
    Minimized = 2,

    /// <summary>The window is maximised.</summary>
    Maximized = 4,

    /// <summary>The window has keyboard focus.</summary>
    InputFocus = 8,

    /// <summary>The mouse is over the window.</summary>
    MouseFocus = 16,

    /// <summary>The window is completely covered by other windows.</summary>
    Occluded = 32,
}
