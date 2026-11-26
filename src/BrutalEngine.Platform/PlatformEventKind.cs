namespace BrutalEngine.Platform;

/// <summary>What a <c>PlatformEvent</c> reports: the tag that says which payload, if any, the event carries.</summary>
/// <remarks>
/// <para>
/// Each kind has exactly one <c>PlatformEvent</c> factory and the payload views that factory writes
/// (docs/design/platform.md, "The event struct"). <see cref="QuitRequested"/> and the <c>Display*</c> kinds are
/// platform-wide and carry a default <see cref="WindowId"/>; every <c>Window*</c> kind carries the window it is for;
/// key, text and mouse kinds carry the window the OS reported them for, or a default <see cref="WindowId"/> when it
/// reported none.
/// </para>
/// <para>
/// Backed by a <see langword="ushort"/>, at offset 0 of the event. The values are fixed: new kinds are only ever
/// appended, never renumbered.
/// </para>
/// </remarks>
public enum PlatformEventKind : ushort
{
    /// <summary>No event (value 0), the default. No factory accepts it.</summary>
    None = 0,

    /// <summary>
    /// The application was asked to quit (for example, the last window closed or the OS is shutting down). No payload.
    /// </summary>
    QuitRequested = 1,

    /// <summary>The window was shown. No payload.</summary>
    WindowShown = 2,

    /// <summary>The window was hidden. No payload.</summary>
    WindowHidden = 3,

    /// <summary>The window moved. No payload: there is no window-position API yet.</summary>
    WindowMoved = 4,

    /// <summary>The window's size changed. Payload: <c>PlatformEvent.Size</c>, in window coordinates.</summary>
    WindowResized = 5,

    /// <summary>The window's size in pixels changed. Payload: <c>PlatformEvent.Size</c>, in pixels.</summary>
    WindowPixelSizeChanged = 6,

    /// <summary>The window's display scale changed. Payload: <c>PlatformEvent.Scale</c>.</summary>
    WindowDisplayScaleChanged = 7,

    /// <summary>The window moved to another display. Payload: <c>PlatformEvent.Display</c>.</summary>
    WindowDisplayChanged = 8,

    /// <summary>The window was minimised. No payload.</summary>
    WindowMinimized = 9,

    /// <summary>The window was maximised. No payload.</summary>
    WindowMaximized = 10,

    /// <summary>The window was restored from being minimised or maximised. No payload.</summary>
    WindowRestored = 11,

    /// <summary>The window gained keyboard focus. No payload.</summary>
    WindowFocusGained = 12,

    /// <summary>The window lost keyboard focus. No payload.</summary>
    WindowFocusLost = 13,

    /// <summary>The mouse entered the window. No payload.</summary>
    WindowMouseEntered = 14,

    /// <summary>The mouse left the window. No payload.</summary>
    WindowMouseLeft = 15,

    /// <summary>The window became completely covered by other windows. No payload.</summary>
    WindowOccluded = 16,

    /// <summary>The window became visible again and must be redrawn. No payload.</summary>
    WindowExposed = 17,

    /// <summary>The user asked to close the window (for example, with its close button). No payload.</summary>
    WindowCloseRequested = 18,

    /// <summary>A key was pressed, or repeated while held. Payload: <c>PlatformEvent.Key</c>.</summary>
    KeyDown = 19,

    /// <summary>A key was released. Payload: <c>PlatformEvent.Key</c>.</summary>
    KeyUp = 20,

    /// <summary>Text was committed while text input was on. Payload: <c>PlatformEvent.Text</c>.</summary>
    TextInput = 21,

    /// <summary>The mouse moved. Payload: <c>PlatformEvent.MouseMotion</c>.</summary>
    MouseMotion = 22,

    /// <summary>A mouse button was pressed. Payload: <c>PlatformEvent.MouseButton</c>.</summary>
    MouseButtonDown = 23,

    /// <summary>A mouse button was released. Payload: <c>PlatformEvent.MouseButton</c>.</summary>
    MouseButtonUp = 24,

    /// <summary>The mouse wheel scrolled. Payload: <c>PlatformEvent.MouseWheel</c>.</summary>
    MouseWheel = 25,

    /// <summary>A display was connected. Payload: <c>PlatformEvent.Display</c>.</summary>
    DisplayAdded = 26,

    /// <summary>A display was disconnected. Payload: <c>PlatformEvent.Display</c>.</summary>
    DisplayRemoved = 27,

    /// <summary>
    /// A display's content scale changed. Payload: both <c>PlatformEvent.Display</c> and
    /// <c>PlatformEvent.Scale</c>.
    /// </summary>
    DisplayContentScaleChanged = 28,

    /// <summary>A display's current mode changed. Payload: <c>PlatformEvent.Display</c>.</summary>
    DisplayCurrentModeChanged = 29,
}
