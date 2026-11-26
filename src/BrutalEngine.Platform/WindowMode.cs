namespace BrutalEngine.Platform;

/// <summary>How a window occupies the screen.</summary>
/// <remarks>The values are fixed: new modes are appended, never renumbered.</remarks>
public enum WindowMode
{
    /// <summary>A normal window on the desktop. The default.</summary>
    Windowed = 0,

    /// <summary>
    /// A borderless window covering its whole display, at the display's current mode. Set with
    /// <see cref="IWindow.SetMode"/>.
    /// </summary>
    BorderlessFullscreen = 1,

    /// <summary>
    /// Fullscreen in a display mode of its own. Entered only with <see cref="IWindow.SetExclusiveFullscreen"/>, which
    /// takes the <see cref="DisplayMode"/> to use.
    /// </summary>
    ExclusiveFullscreen = 2,
}
