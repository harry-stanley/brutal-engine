using System;
using System.Runtime.InteropServices;
using System.Text.Unicode;
using Brutal.SdlApi;

namespace BrutalEngine.Platform.Sdl3;

/// <summary>
/// Turns one SDL event into the matching <see cref="PlatformEvent"/> in a <see cref="PlatformEventBuffer"/>, or ignores
/// it (docs/design/platform.md section 2, "The event struct").
/// </summary>
/// <remarks>
/// <para>
/// Each SDL type the engine uses maps to exactly one <see cref="PlatformEventKind"/>. Every other SDL type is ignored
/// and adds nothing: gamepads, joysticks, touch, pen, drops, IME editing, audio and camera devices, keyboard and mouse
/// hot-plug, the window and display types without a kind, user events and types SDL may add later. Nothing here throws
/// for any SDL event: a window event SDL reports without a window (id 0), which no <c>Window*</c> kind can carry, is
/// ignored, and so is a scale event whose scale can't be read.
/// </para>
/// <para>
/// <b>Header:</b> the timestamp is the caller's, from the platform clock at translation, never SDL's own event time.
/// The window is SDL's window id for the <c>Window*</c> kinds, and default for
/// <see cref="PlatformEventKind.QuitRequested"/> and the <c>Display*</c> kinds.
/// </para>
/// <para>
/// <b>Payloads:</b> <c>WindowResized</c> and <c>WindowPixelSizeChanged</c> carry SDL's <c>data1</c> by <c>data2</c>
/// (window coordinates and pixels); <c>WindowDisplayChanged</c> carries the display id SDL sends in <c>data1</c>; the
/// <c>Display*</c> kinds carry SDL's display id. SDL's two scale events carry no scale (SDL sends them with data 0, 0),
/// so <c>WindowDisplayScaleChanged</c> and <c>DisplayContentScaleChanged</c> read the current scale through an
/// <see cref="IScaleSource"/>: <see cref="SdlScaleSource"/> in the backend, a fake in tests. Everything else is
/// pure.
/// </para>
/// <para>
/// <b>Keys:</b> key events carry SDL's window, or a default one when SDL reports none. The scancode is SDL's value,
/// kept after a range check (a value above <see cref="ushort.MaxValue"/> becomes <see cref="Scancode.Unknown"/>); the
/// keycode and the modifier bits pass through unchanged, because the engine's encodings are SDL's
/// (docs/design/platform.md, "Keys"). <see cref="KeyEventData.IsRepeat"/> is SDL's <c>repeat</c> flag.
/// </para>
/// <para>
/// <b>Text:</b> SDL's NUL-terminated UTF-8 is copied into the buffer's text storage by
/// <see cref="PlatformEventBuffer.AddText"/> before this returns (SDL frees it at a later event call), and an
/// over-long text is cut at a code-point boundary and counted there. SDL doesn't validate the text it queues, so a
/// text that isn't valid UTF-8 is ignored, as is a null text pointer; an empty text adds an empty event.
/// </para>
/// <para>
/// <b>Mouse:</b> mouse events carry SDL's window, or a default one when SDL reports none. Positions and motion are
/// SDL's float window coordinates. The button number and the held-button state are cast, because the engine's values
/// are SDL's; the state narrows to a byte, dropping buttons 9 and above. The wheel always uses SDL's normal
/// convention: positive <see cref="MouseWheelData.Y"/> scrolls away from the user and positive
/// <see cref="MouseWheelData.X"/> scrolls right. When SDL reports a wheel event as flipped
/// (<c>SDL_MOUSEWHEEL_FLIPPED</c>, a "natural scrolling" setting), both amounts are negated back; the pointer position
/// is never negated.
/// </para>
/// <para>
/// <b>Threads:</b> call on the thread that pumps the buffer, which owns it; with <see cref="SdlScaleSource"/>, the
/// thread that started SDL video. Stateless. <b>Allocation:</b> none.
/// </para>
/// </remarks>
internal static class SdlEventTranslator
{
    /// <summary>
    /// Reads the current scales that SDL's scale events refer to but don't carry.
    /// </summary>
    /// <remarks>
    /// Implemented by structs, which <see cref="TryAdd{TScales}"/> calls through a generic constraint, so the calls
    /// never box. An implementation must not allocate.
    /// </remarks>
    internal interface IScaleSource
    {
        /// <summary>Reads a window's current display scale.</summary>
        /// <param name="window">The window, by SDL's id.</param>
        /// <param name="scale">The display scale when this returns <see langword="true"/>; otherwise 0.</param>
        /// <returns><see langword="true"/> if the window exists and its scale was read.</returns>
        public bool TryGetWindowDisplayScale(WindowId window, out float scale);

        /// <summary>Reads a display's current content scale.</summary>
        /// <param name="display">The display, by SDL's id.</param>
        /// <param name="scale">The content scale when this returns <see langword="true"/>; otherwise 0.</param>
        /// <returns><see langword="true"/> if the display is connected and its scale was read.</returns>
        public bool TryGetDisplayContentScale(DisplayId display, out float scale);
    }

    /// <summary>
    /// Adds the <see cref="PlatformEvent"/> that <paramref name="sdlEvent"/> stands for to <paramref name="buffer"/>,
    /// or ignores it.
    /// </summary>
    /// <typeparam name="TScales">The scale source's type: a struct, so its calls aren't boxed.</typeparam>
    /// <param name="sdlEvent">The SDL event, as <c>SDL_PollEvent</c> returned it.</param>
    /// <param name="buffer">
    /// The pump's buffer. The caller checks <see cref="PlatformEventBuffer.HasRoom"/> before dequeuing the event.
    /// </param>
    /// <param name="timestamp">When the pump translated the event, from the platform clock.</param>
    /// <param name="scales">Where the two scale events read the scale; asked only for those events.</param>
    /// <returns>
    /// <see langword="true"/> if one event was added; <see langword="false"/> if the SDL event was ignored, which
    /// leaves the buffer unchanged.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="buffer"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// The buffer already holds its capacity of events (from <see cref="PlatformEventBuffer.Add"/>). Nothing is added.
    /// </exception>
    public static bool TryAdd<TScales>(
        in SdlEvent sdlEvent, PlatformEventBuffer buffer, long timestamp, ref TScales scales)
        where TScales : struct, IScaleSource
    {
        ArgumentNullException.ThrowIfNull(buffer);
        switch (sdlEvent.type)
        {
            case SdlEventType.Quit:
                buffer.Add(PlatformEvent.Create(new(PlatformEventKind.QuitRequested, default, timestamp)));
                return true;

            case SdlEventType.DisplayAdded:
            case SdlEventType.DisplayRemoved:
            case SdlEventType.DisplayCurrentModeChanged:
                buffer.Add(PlatformEvent.CreateDisplay(
                    new(DisplayKindOf(sdlEvent.type), default, timestamp), new DisplayId(sdlEvent.display.displayID)));
                return true;

            case SdlEventType.DisplayContentScaleChanged:
                return AddDisplayScale(sdlEvent.display, buffer, timestamp, ref scales);

            case SdlEventType.KeyDown:
            case SdlEventType.KeyUp:
                AddKey(sdlEvent.key, buffer, timestamp);
                return true;

            case SdlEventType.TextInput:
                return AddText(sdlEvent.text, buffer, timestamp);

            case SdlEventType.MouseMotion:
                AddMotion(sdlEvent.motion, buffer, timestamp);
                return true;

            case SdlEventType.MouseButtonDown:
            case SdlEventType.MouseButtonUp:
                AddButton(sdlEvent.button, buffer, timestamp);
                return true;

            case SdlEventType.MouseWheel:
                AddWheel(sdlEvent.wheel, buffer, timestamp);
                return true;

            default:
                return sdlEvent.type is >= SdlEventType.WindowFirst and <= SdlEventType.WindowLast
                    && AddWindow(sdlEvent.window, buffer, timestamp, ref scales);
        }
    }

    /// <summary>Converts an SDL scancode: a range check plus a cast, because the engine's values are SDL's.</summary>
    /// <param name="scancode">SDL's scancode. Any value is accepted.</param>
    /// <returns>
    /// The same value, including values without a member; <see cref="Scancode.Unknown"/> if it doesn't fit in a
    /// <see langword="ushort"/>.
    /// </returns>
    private static Scancode ToScancode(SdlScancode scancode) =>
        (uint)scancode <= ushort.MaxValue ? (Scancode)(ushort)scancode : Scancode.Unknown;

    // DisplayContentScaleChanged has its own case.
    private static PlatformEventKind DisplayKindOf(SdlEventType type) => type switch
    {
        SdlEventType.DisplayAdded => PlatformEventKind.DisplayAdded,
        SdlEventType.DisplayRemoved => PlatformEventKind.DisplayRemoved,
        _ => PlatformEventKind.DisplayCurrentModeChanged,
    };

    // The kind of each SDL window type the engine maps; None for the others (Metal view, hit test, ICC profile, safe
    // area, fullscreen enter and leave, destroyed, HDR state).
    private static PlatformEventKind WindowKindOf(SdlEventType type) => type switch
    {
        SdlEventType.WindowShown => PlatformEventKind.WindowShown,
        SdlEventType.WindowHidden => PlatformEventKind.WindowHidden,
        SdlEventType.WindowExposed => PlatformEventKind.WindowExposed,
        SdlEventType.WindowMoved => PlatformEventKind.WindowMoved,
        SdlEventType.WindowResized => PlatformEventKind.WindowResized,
        SdlEventType.WindowPixelSizeChanged => PlatformEventKind.WindowPixelSizeChanged,
        SdlEventType.WindowMinimized => PlatformEventKind.WindowMinimized,
        SdlEventType.WindowMaximized => PlatformEventKind.WindowMaximized,
        SdlEventType.WindowRestored => PlatformEventKind.WindowRestored,
        SdlEventType.WindowMouseEnter => PlatformEventKind.WindowMouseEntered,
        SdlEventType.WindowMouseLeave => PlatformEventKind.WindowMouseLeft,
        SdlEventType.WindowFocusGained => PlatformEventKind.WindowFocusGained,
        SdlEventType.WindowFocusLost => PlatformEventKind.WindowFocusLost,
        SdlEventType.WindowCloseRequested => PlatformEventKind.WindowCloseRequested,
        SdlEventType.WindowDisplayChanged => PlatformEventKind.WindowDisplayChanged,
        SdlEventType.WindowDisplayScaleChanged => PlatformEventKind.WindowDisplayScaleChanged,
        SdlEventType.WindowOccluded => PlatformEventKind.WindowOccluded,
        _ => PlatformEventKind.None,
    };

    private static bool AddWindow<TScales>(
        in SdlWindowEvent e, PlatformEventBuffer buffer, long timestamp, ref TScales scales)
        where TScales : struct, IScaleSource
    {
        PlatformEventKind kind = WindowKindOf(e.type);
        if (kind == PlatformEventKind.None || e.windowID == 0)
        {
            return false;
        }

        WindowId window = new(e.windowID);
        PlatformEventHeader header = new(kind, window, timestamp);
        switch (kind)
        {
            case PlatformEventKind.WindowResized:
            case PlatformEventKind.WindowPixelSizeChanged:
                buffer.Add(PlatformEvent.CreateSize(header, new WindowSize(e.data1, e.data2)));
                return true;

            case PlatformEventKind.WindowDisplayChanged:
                // SDL sends the display id, a uint, cast to int in data1: the cast back restores it.
                buffer.Add(PlatformEvent.CreateDisplay(header, new DisplayId(unchecked((uint)e.data1))));
                return true;

            case PlatformEventKind.WindowDisplayScaleChanged:
                if (!scales.TryGetWindowDisplayScale(window, out float scale))
                {
                    return false;
                }

                buffer.Add(PlatformEvent.CreateScale(header, scale));
                return true;

            default:
                buffer.Add(PlatformEvent.Create(header));
                return true;
        }
    }

    private static bool AddDisplayScale<TScales>(
        in SdlDisplayEvent e, PlatformEventBuffer buffer, long timestamp, ref TScales scales)
        where TScales : struct, IScaleSource
    {
        DisplayId display = new(e.displayID);
        if (!scales.TryGetDisplayContentScale(display, out float scale))
        {
            return false;
        }

        PlatformEventHeader header = new(PlatformEventKind.DisplayContentScaleChanged, default, timestamp);
        buffer.Add(PlatformEvent.CreateDisplayScale(header, display, scale));
        return true;
    }

    private static void AddKey(in SdlKeyboardEvent e, PlatformEventBuffer buffer, long timestamp)
    {
        PlatformEventKind kind = e.type == SdlEventType.KeyDown ? PlatformEventKind.KeyDown : PlatformEventKind.KeyUp;
        KeyEventData key = new(ToScancode(e.scancode), new Keycode((uint)e.key), (KeyModifiers)e.mod, e.repeat != 0);
        buffer.Add(PlatformEvent.CreateKey(new(kind, new WindowId(e.windowID), timestamp), key));
    }

    private static unsafe bool AddText(in SdlTextInputEvent e, PlatformEventBuffer buffer, long timestamp)
    {
        if (e.text is null)
        {
            return false;
        }

        // Sound: SDL's text is a NUL-terminated string that stays valid until the next SDL event call, and the span
        // over it is read (and copied by AddText) before this returns.
        ReadOnlySpan<byte> utf8 = MemoryMarshal.CreateReadOnlySpanFromNullTerminated(e.text);
        if (!Utf8.IsValid(utf8))
        {
            return false;
        }

        buffer.AddText(new WindowId(e.windowID), timestamp, utf8);
        return true;
    }

    private static void AddMotion(in SdlMouseMotionEvent e, PlatformEventBuffer buffer, long timestamp)
    {
        // The cast to byte keeps buttons 1-8, the bits MouseButtons can hold.
        MouseMotionData motion = new(e.x, e.y, e.xrel, e.yrel, (MouseButtons)(byte)e.state);
        PlatformEventHeader header = new(PlatformEventKind.MouseMotion, new WindowId(e.windowID), timestamp);
        buffer.Add(PlatformEvent.CreateMouseMotion(header, motion));
    }

    private static void AddButton(in SdlMouseButtonEvent e, PlatformEventBuffer buffer, long timestamp)
    {
        PlatformEventKind kind = e.type == SdlEventType.MouseButtonDown
            ? PlatformEventKind.MouseButtonDown
            : PlatformEventKind.MouseButtonUp;
        MouseButtonData button = new((MouseButton)e.button, e.clicks, e.x, e.y);
        buffer.Add(PlatformEvent.CreateMouseButton(new(kind, new WindowId(e.windowID), timestamp), button));
    }

    private static void AddWheel(in SdlMouseWheelEvent e, PlatformEventBuffer buffer, long timestamp)
    {
        // SDL_events.h: "When FLIPPED the values in X and Y will be opposite. Multiply by -1 to change them back".
        float sign = e.direction == SdlMouseWheelDirection.Flipped ? -1f : 1f;
        MouseWheelData wheel = new(e.x * sign, e.y * sign, e.mouse_x, e.mouse_y);
        PlatformEventHeader header = new(PlatformEventKind.MouseWheel, new WindowId(e.windowID), timestamp);
        buffer.Add(PlatformEvent.CreateMouseWheel(header, wheel));
    }

    /// <summary>
    /// The backend's scale source: reads the scales from SDL, which keeps them current as it queues the scale events.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A window's scale is <c>SDL_GetWindowDisplayScale</c> of <c>SDL_GetWindowFromID</c>; a display's is
    /// <c>SDL_GetDisplayContentScale</c>. SDL reports failure as a null window or a scale of 0, which happens when the
    /// window was destroyed or the display unplugged after SDL queued the event; the query then fails, and the event
    /// is ignored. SDL's error text isn't read: an ignored event is expected, not an error.
    /// </para>
    /// <para>
    /// <b>Threads:</b> the thread that started SDL video, as every SDL video call. Stateless. <b>Allocation:</b> none.
    /// </para>
    /// </remarks>
    internal readonly struct SdlScaleSource : IScaleSource
    {
        /// <inheritdoc/>
        public bool TryGetWindowDisplayScale(WindowId window, out float scale)
        {
            SdlWindow handle = Sdl.PInvoke.GetWindowFromID(window.Value);
            scale = handle.IsNull() ? 0f : Sdl.PInvoke.GetWindowDisplayScale(handle);
            return scale != 0f;
        }

        /// <inheritdoc/>
        public bool TryGetDisplayContentScale(DisplayId display, out float scale)
        {
            scale = Sdl.PInvoke.GetDisplayContentScale(display.Value);
            return scale != 0f;
        }
    }
}
