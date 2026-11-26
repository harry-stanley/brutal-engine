using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace BrutalEngine.Platform;

/// <summary>
/// One platform event: a 16-byte header (kind, window and timestamp) plus a 24-byte payload whose meaning depends on
/// <see cref="Kind"/>, 40 bytes in all.
/// </summary>
/// <remarks>
/// <para>
/// A tagged union with no references, so a buffer of events is one flat array that the garbage collector never scans.
/// The header is at offsets 0 to 15: <see cref="Kind"/> at 0 (offsets 2 and 3 are reserved and zero),
/// <see cref="Window"/> at 4 and <see cref="Timestamp"/> at 8. The payload views overlap from offset 16, as in
/// docs/design/platform.md ("The event struct"): <see cref="Key"/> at 16-27, <see cref="Text"/> at 16-23,
/// <see cref="MouseMotion"/> at 16-35, <see cref="MouseButton"/> at 16-27, <see cref="MouseWheel"/> at 16-31,
/// <see cref="Size"/> at 16-23, <see cref="Display"/> at 16-19 and <see cref="Scale"/> at 20-23. Bytes a kind's
/// payload doesn't use are zero.
/// </para>
/// <para>
/// Build events with the factories, one per payload, which check the header: the kind must be one of the factory's
/// kinds, a <c>Window*</c> kind needs a non-default <see cref="Window"/>, and
/// <see cref="PlatformEventKind.QuitRequested"/> and the <c>Display*</c> kinds need a default one. Key, text and
/// mouse kinds take either. Reading a view the kind doesn't carry throws <see cref="InvalidOperationException"/>; so
/// does every view of <c>default(PlatformEvent)</c>, whose kind is <see cref="PlatformEventKind.None"/>.
/// </para>
/// <para>
/// The factories store payload values as given: they don't check a <see cref="DisplayId"/> against the connected
/// displays or a scale for being finite or positive. The backend that produces the events owns the values' validity.
/// </para>
/// <para>
/// Immutable and safe to read from any thread. No member allocates, except the members that throw and
/// <see cref="ValueType.ToString"/>, which is the default one and returns the type's name.
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Explicit, Size = 40)]
public readonly struct PlatformEvent
{
    [FieldOffset(0)]
    private readonly PlatformEventKind _kind;

    [FieldOffset(4)]
    private readonly WindowId _window;

    [FieldOffset(8)]
    private readonly long _timestamp;

    [FieldOffset(16)]
    private readonly KeyEventData _key;

    [FieldOffset(16)]
    private readonly TextEventData _text;

    [FieldOffset(16)]
    private readonly MouseMotionData _mouseMotion;

    [FieldOffset(16)]
    private readonly MouseButtonData _mouseButton;

    [FieldOffset(16)]
    private readonly MouseWheelData _mouseWheel;

    [FieldOffset(16)]
    private readonly WindowSize _size;

    [FieldOffset(16)]
    private readonly DisplayId _display;

    [FieldOffset(20)]
    private readonly float _scale;

    private PlatformEvent(PlatformEventHeader header)
    {
        // Zeroes the reserved bytes and every payload byte the kind doesn't use.
        this = default;
        _kind = header.Kind;
        _window = header.Window;
        _timestamp = header.Timestamp;
    }

    private PlatformEvent(PlatformEventHeader header, KeyEventData key)
        : this(header) => _key = key;

    private PlatformEvent(PlatformEventHeader header, TextEventData text)
        : this(header) => _text = text;

    private PlatformEvent(PlatformEventHeader header, MouseMotionData motion)
        : this(header) => _mouseMotion = motion;

    private PlatformEvent(PlatformEventHeader header, MouseButtonData button)
        : this(header) => _mouseButton = button;

    private PlatformEvent(PlatformEventHeader header, MouseWheelData wheel)
        : this(header) => _mouseWheel = wheel;

    private PlatformEvent(PlatformEventHeader header, WindowSize size)
        : this(header) => _size = size;

    private PlatformEvent(PlatformEventHeader header, DisplayId display)
        : this(header) => _display = display;

    private PlatformEvent(PlatformEventHeader header, float scale)
        : this(header) => _scale = scale;

    private PlatformEvent(PlatformEventHeader header, DisplayId display, float scale)
        : this(header)
    {
        _display = display;
        _scale = scale;
    }

    // Which factory and views a kind has: the kind-to-factory table in docs/design/platform.md, "The event struct".
    private enum Payload : byte
    {
        Invalid = 0,
        None,
        Key,
        Text,
        MouseMotion,
        MouseButton,
        MouseWheel,
        Size,
        Display,
        Scale,
        DisplayScale,
    }

    // What a kind's header Window must be.
    private enum WindowRule : byte
    {
        Any = 0,
        Required,
        Default,
    }

    /// <summary>Gets what the event reports, which decides the payload views it carries (offset 0).</summary>
    /// <value>The kind; <see cref="PlatformEventKind.None"/> for <c>default(PlatformEvent)</c>.</value>
    public PlatformEventKind Kind => _kind;

    /// <summary>Gets the window the event is for (offset 4).</summary>
    /// <value>
    /// The window; default for <see cref="PlatformEventKind.QuitRequested"/> and the <c>Display*</c> kinds, and for
    /// key, text and mouse events the OS reported without a window.
    /// </value>
    public WindowId Window => _window;

    /// <summary>Gets when the backend's pump translated the event (offset 8).</summary>
    /// <value>
    /// A timestamp from the platform clock's <see cref="TimeProvider.GetTimestamp"/>, in that clock's ticks.
    /// </value>
    public long Timestamp => _timestamp;

    /// <summary>
    /// Gets the header: <see cref="Kind"/>, <see cref="Window"/> and <see cref="Timestamp"/> together.
    /// </summary>
    /// <value>The header the event was created with.</value>
    public PlatformEventHeader Header => new(_kind, _window, _timestamp);

    /// <summary>Gets the key payload of a <c>KeyDown</c> or <c>KeyUp</c> event (offsets 16-27).</summary>
    /// <value>The key, its keycode, the modifiers and whether it is a repeat.</value>
    /// <exception cref="InvalidOperationException"><see cref="Kind"/> is not a key kind.</exception>
    public KeyEventData Key => PayloadOf(_kind) == Payload.Key ? _key : throw WrongView(nameof(Key));

    /// <summary>Gets the text payload of a <c>TextInput</c> event (offsets 16-23).</summary>
    /// <value>Where the event's UTF-8 text sits in the text storage of the buffer that holds the event.</value>
    /// <exception cref="InvalidOperationException">
    /// <see cref="Kind"/> is not <see cref="PlatformEventKind.TextInput"/>.
    /// </exception>
    public TextEventData Text => PayloadOf(_kind) == Payload.Text ? _text : throw WrongView(nameof(Text));

    /// <summary>Gets the motion payload of a <c>MouseMotion</c> event (offsets 16-35).</summary>
    /// <value>The pointer position and motion, in window coordinates, and the buttons held.</value>
    /// <exception cref="InvalidOperationException">
    /// <see cref="Kind"/> is not <see cref="PlatformEventKind.MouseMotion"/>.
    /// </exception>
    public MouseMotionData MouseMotion =>
        PayloadOf(_kind) == Payload.MouseMotion ? _mouseMotion : throw WrongView(nameof(MouseMotion));

    /// <summary>
    /// Gets the button payload of a <c>MouseButtonDown</c> or <c>MouseButtonUp</c> event (offsets 16-27).
    /// </summary>
    /// <value>The button, the click count and the pointer position.</value>
    /// <exception cref="InvalidOperationException"><see cref="Kind"/> is not a mouse button kind.</exception>
    public MouseButtonData MouseButton =>
        PayloadOf(_kind) == Payload.MouseButton ? _mouseButton : throw WrongView(nameof(MouseButton));

    /// <summary>Gets the wheel payload of a <c>MouseWheel</c> event (offsets 16-31).</summary>
    /// <value>The scroll amounts and the pointer position.</value>
    /// <exception cref="InvalidOperationException">
    /// <see cref="Kind"/> is not <see cref="PlatformEventKind.MouseWheel"/>.
    /// </exception>
    public MouseWheelData MouseWheel =>
        PayloadOf(_kind) == Payload.MouseWheel ? _mouseWheel : throw WrongView(nameof(MouseWheel));

    /// <summary>
    /// Gets the new size of a <c>WindowResized</c> event (window coordinates) or a <c>WindowPixelSizeChanged</c>
    /// event (pixels) (offsets 16-23).
    /// </summary>
    /// <value>The window's new size, in the unit the kind names.</value>
    /// <exception cref="InvalidOperationException"><see cref="Kind"/> is not a size kind.</exception>
    public WindowSize Size => PayloadOf(_kind) == Payload.Size ? _size : throw WrongView(nameof(Size));

    /// <summary>
    /// Gets the display of a <c>WindowDisplayChanged</c>, <c>DisplayAdded</c>, <c>DisplayRemoved</c>,
    /// <c>DisplayCurrentModeChanged</c> or <c>DisplayContentScaleChanged</c> event (offsets 16-19).
    /// </summary>
    /// <value>The display the window moved to, or the display the event is about.</value>
    /// <exception cref="InvalidOperationException"><see cref="Kind"/> doesn't carry a display.</exception>
    public DisplayId Display =>
        PayloadOf(_kind) is Payload.Display or Payload.DisplayScale ? _display : throw WrongView(nameof(Display));

    /// <summary>
    /// Gets the new scale of a <c>WindowDisplayScaleChanged</c> event (the window's display scale) or a
    /// <c>DisplayContentScaleChanged</c> event (the display's content scale) (offsets 20-23).
    /// </summary>
    /// <value>The new scale, as the backend reported it.</value>
    /// <exception cref="InvalidOperationException"><see cref="Kind"/> doesn't carry a scale.</exception>
    public float Scale =>
        PayloadOf(_kind) is Payload.Scale or Payload.DisplayScale ? _scale : throw WrongView(nameof(Scale));

    /// <summary>
    /// Creates an event without a payload: <see cref="PlatformEventKind.QuitRequested"/> or a <c>Window*</c> kind
    /// with no data (shown, hidden, moved, minimised, maximised, restored, focus, mouse enter and leave, occluded,
    /// exposed, close requested).
    /// </summary>
    /// <param name="header">The kind, window and timestamp.</param>
    /// <returns>The event. Its payload bytes are zero.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="header"/>'s kind isn't one of this factory's kinds, or breaks the kind's window rule.
    /// </exception>
    public static PlatformEvent Create(PlatformEventHeader header)
    {
        Check(header, Payload.None);
        return new PlatformEvent(header);
    }

    /// <summary>Creates a <c>KeyDown</c> or <c>KeyUp</c> event.</summary>
    /// <param name="header">The kind, window (default when the OS reported none) and timestamp.</param>
    /// <param name="key">The key payload, stored as given.</param>
    /// <returns>The event.</returns>
    /// <exception cref="ArgumentException"><paramref name="header"/>'s kind isn't a key kind.</exception>
    public static PlatformEvent CreateKey(PlatformEventHeader header, KeyEventData key)
    {
        Check(header, Payload.Key);
        return new PlatformEvent(header, key);
    }

    /// <summary>
    /// Creates a <c>TextInput</c> event. Backends normally add text through the event buffer, which stores the bytes
    /// and makes the payload.
    /// </summary>
    /// <param name="header">The kind, window (default when the OS reported none) and timestamp.</param>
    /// <param name="text">The text payload, stored as given: this factory doesn't check it against any buffer.</param>
    /// <returns>The event.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="header"/>'s kind isn't <see cref="PlatformEventKind.TextInput"/>.
    /// </exception>
    public static PlatformEvent CreateText(PlatformEventHeader header, TextEventData text)
    {
        Check(header, Payload.Text);
        return new PlatformEvent(header, text);
    }

    /// <summary>Creates a <c>MouseMotion</c> event.</summary>
    /// <param name="header">The kind, window (default when the OS reported none) and timestamp.</param>
    /// <param name="motion">The motion payload, stored as given.</param>
    /// <returns>The event.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="header"/>'s kind isn't <see cref="PlatformEventKind.MouseMotion"/>.
    /// </exception>
    public static PlatformEvent CreateMouseMotion(PlatformEventHeader header, MouseMotionData motion)
    {
        Check(header, Payload.MouseMotion);
        return new PlatformEvent(header, motion);
    }

    /// <summary>Creates a <c>MouseButtonDown</c> or <c>MouseButtonUp</c> event.</summary>
    /// <param name="header">The kind, window (default when the OS reported none) and timestamp.</param>
    /// <param name="button">The button payload, stored as given.</param>
    /// <returns>The event.</returns>
    /// <exception cref="ArgumentException"><paramref name="header"/>'s kind isn't a mouse button kind.</exception>
    public static PlatformEvent CreateMouseButton(PlatformEventHeader header, MouseButtonData button)
    {
        Check(header, Payload.MouseButton);
        return new PlatformEvent(header, button);
    }

    /// <summary>Creates a <c>MouseWheel</c> event.</summary>
    /// <param name="header">The kind, window (default when the OS reported none) and timestamp.</param>
    /// <param name="wheel">The wheel payload, stored as given.</param>
    /// <returns>The event.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="header"/>'s kind isn't <see cref="PlatformEventKind.MouseWheel"/>.
    /// </exception>
    public static PlatformEvent CreateMouseWheel(PlatformEventHeader header, MouseWheelData wheel)
    {
        Check(header, Payload.MouseWheel);
        return new PlatformEvent(header, wheel);
    }

    /// <summary>Creates a <c>WindowResized</c> or <c>WindowPixelSizeChanged</c> event.</summary>
    /// <param name="header">The kind, a non-default window and the timestamp.</param>
    /// <param name="size">
    /// The new size (window coordinates for <c>WindowResized</c>, pixels for <c>WindowPixelSizeChanged</c>), stored
    /// as given.
    /// </param>
    /// <returns>The event.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="header"/>'s kind isn't a size kind, or its window is default.
    /// </exception>
    public static PlatformEvent CreateSize(PlatformEventHeader header, WindowSize size)
    {
        Check(header, Payload.Size);
        return new PlatformEvent(header, size);
    }

    /// <summary>
    /// Creates a <c>WindowDisplayChanged</c> event (with a non-default window) or a <c>DisplayAdded</c>,
    /// <c>DisplayRemoved</c> or <c>DisplayCurrentModeChanged</c> event (with a default window).
    /// </summary>
    /// <param name="header">The kind, window and timestamp.</param>
    /// <param name="display">The display, stored as given: any <see cref="DisplayId"/> is accepted.</param>
    /// <returns>The event.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="header"/>'s kind isn't one of this factory's kinds, or breaks the kind's window rule.
    /// </exception>
    public static PlatformEvent CreateDisplay(PlatformEventHeader header, DisplayId display)
    {
        Check(header, Payload.Display);
        return new PlatformEvent(header, display);
    }

    /// <summary>Creates a <c>WindowDisplayScaleChanged</c> event.</summary>
    /// <param name="header">The kind, a non-default window and the timestamp.</param>
    /// <param name="scale">
    /// The window's new display scale, stored as given: any value, NaN included, is accepted.
    /// </param>
    /// <returns>The event.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="header"/>'s kind isn't <see cref="PlatformEventKind.WindowDisplayScaleChanged"/>, or its window
    /// is default.
    /// </exception>
    public static PlatformEvent CreateScale(PlatformEventHeader header, float scale)
    {
        Check(header, Payload.Scale);
        return new PlatformEvent(header, scale);
    }

    /// <summary>Creates a <c>DisplayContentScaleChanged</c> event, which carries both a display and a scale.</summary>
    /// <param name="header">The kind, a default window and the timestamp.</param>
    /// <param name="display">The display whose content scale changed, stored as given.</param>
    /// <param name="scale">
    /// The display's new content scale, stored as given: any value, NaN included, is accepted.
    /// </param>
    /// <returns>The event.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="header"/>'s kind isn't <see cref="PlatformEventKind.DisplayContentScaleChanged"/>, or its
    /// window isn't default.
    /// </exception>
    public static PlatformEvent CreateDisplayScale(PlatformEventHeader header, DisplayId display, float scale)
    {
        Check(header, Payload.DisplayScale);
        return new PlatformEvent(header, display, scale);
    }

    private static Payload PayloadOf(PlatformEventKind kind) => kind switch
    {
        PlatformEventKind.QuitRequested or PlatformEventKind.WindowShown or PlatformEventKind.WindowHidden
            or PlatformEventKind.WindowMoved or PlatformEventKind.WindowMinimized or PlatformEventKind.WindowMaximized
            or PlatformEventKind.WindowRestored or PlatformEventKind.WindowFocusGained
            or PlatformEventKind.WindowFocusLost or PlatformEventKind.WindowMouseEntered
            or PlatformEventKind.WindowMouseLeft or PlatformEventKind.WindowOccluded or PlatformEventKind.WindowExposed
            or PlatformEventKind.WindowCloseRequested => Payload.None,
        PlatformEventKind.KeyDown or PlatformEventKind.KeyUp => Payload.Key,
        PlatformEventKind.TextInput => Payload.Text,
        PlatformEventKind.MouseMotion => Payload.MouseMotion,
        PlatformEventKind.MouseButtonDown or PlatformEventKind.MouseButtonUp => Payload.MouseButton,
        PlatformEventKind.MouseWheel => Payload.MouseWheel,
        PlatformEventKind.WindowResized or PlatformEventKind.WindowPixelSizeChanged => Payload.Size,
        PlatformEventKind.WindowDisplayChanged or PlatformEventKind.DisplayAdded or PlatformEventKind.DisplayRemoved
            or PlatformEventKind.DisplayCurrentModeChanged => Payload.Display,
        PlatformEventKind.WindowDisplayScaleChanged => Payload.Scale,
        PlatformEventKind.DisplayContentScaleChanged => Payload.DisplayScale,

        // None and values without a member.
        _ => Payload.Invalid,
    };

    // Only called for kinds PayloadOf accepted. Every kind is listed by name, as in PayloadOf, so a kind appended later
    // must be classified here: it can't fall into a range or a default and silently accept any window.
    private static WindowRule WindowRuleOf(PlatformEventKind kind) => kind switch
    {
        PlatformEventKind.QuitRequested or PlatformEventKind.DisplayAdded or PlatformEventKind.DisplayRemoved
            or PlatformEventKind.DisplayContentScaleChanged
            or PlatformEventKind.DisplayCurrentModeChanged => WindowRule.Default,
        PlatformEventKind.WindowShown or PlatformEventKind.WindowHidden or PlatformEventKind.WindowMoved
            or PlatformEventKind.WindowResized or PlatformEventKind.WindowPixelSizeChanged
            or PlatformEventKind.WindowDisplayScaleChanged or PlatformEventKind.WindowDisplayChanged
            or PlatformEventKind.WindowMinimized or PlatformEventKind.WindowMaximized
            or PlatformEventKind.WindowRestored or PlatformEventKind.WindowFocusGained
            or PlatformEventKind.WindowFocusLost or PlatformEventKind.WindowMouseEntered
            or PlatformEventKind.WindowMouseLeft or PlatformEventKind.WindowOccluded or PlatformEventKind.WindowExposed
            or PlatformEventKind.WindowCloseRequested => WindowRule.Required,
        PlatformEventKind.KeyDown or PlatformEventKind.KeyUp or PlatformEventKind.TextInput
            or PlatformEventKind.MouseMotion or PlatformEventKind.MouseButtonDown or PlatformEventKind.MouseButtonUp
            or PlatformEventKind.MouseWheel => WindowRule.Any,
        _ => throw new UnreachableException(string.Create(
            CultureInfo.InvariantCulture, $"PlatformEventKind {kind} has a factory but no window rule.")),
    };

    private static void Check(PlatformEventHeader header, Payload payload)
    {
        Payload actual = PayloadOf(header.Kind);
        if (actual != payload)
        {
            throw new ArgumentException(WrongFactoryMessage(header.Kind, payload, actual), nameof(header));
        }

        WindowRule rule = WindowRuleOf(header.Kind);
        if (rule == WindowRule.Required && header.Window == default)
        {
            throw new ArgumentException(
                WindowMessage(header.Kind, "needs a window: its header's Window can't be default"), nameof(header));
        }

        if (rule == WindowRule.Default && header.Window != default)
        {
            throw new ArgumentException(
                WindowMessage(header.Kind, "is platform-wide: its header's Window must be default"), nameof(header));
        }
    }

    private static string WrongFactoryMessage(PlatformEventKind kind, Payload payload, Payload actual) =>
        actual == Payload.Invalid
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"PlatformEventKind {kind} has no factory, so {FactoryName(payload)} can't create it.")
            : string.Create(
                CultureInfo.InvariantCulture,
                $"PlatformEventKind {kind} is created by {FactoryName(actual)}, not {FactoryName(payload)}.");

    private static string WindowMessage(PlatformEventKind kind, string rule) =>
        string.Create(CultureInfo.InvariantCulture, $"PlatformEventKind {kind} {rule}.");

    private static string FactoryName(Payload payload) => payload switch
    {
        Payload.None => nameof(Create),
        Payload.Key => nameof(CreateKey),
        Payload.Text => nameof(CreateText),
        Payload.MouseMotion => nameof(CreateMouseMotion),
        Payload.MouseButton => nameof(CreateMouseButton),
        Payload.MouseWheel => nameof(CreateMouseWheel),
        Payload.Size => nameof(CreateSize),
        Payload.Display => nameof(CreateDisplay),
        Payload.Scale => nameof(CreateScale),
        _ => nameof(CreateDisplayScale),
    };

    private InvalidOperationException WrongView(string view) => new(string.Create(
        CultureInfo.InvariantCulture,
        $"A {_kind} event doesn't carry {view}: read only the views its kind carries "
        + $"(docs/design/platform.md, \"The event struct\")."));
}
