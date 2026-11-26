using System;
using System.Globalization;
using System.Threading;
using Brutal.SdlApi;
using BrutalEngine.Core;

namespace BrutalEngine.Platform.Sdl3;

/// <summary>
/// One SDL window, the backend's <see cref="IWindow"/> (docs/design/platform.md section 1): created from
/// <see cref="WindowOptions"/> with its size requested in content units, with a snapshot of its sizes and state that the
/// getters read.
/// </summary>
/// <remarks>
/// <para>
/// Not named <c>SdlWindow</c>: that is BRUTAL's window handle, which this namespace uses. The event pump keeps the
/// snapshot current through <see cref="RefreshSnapshot"/>, which it reaches through <see cref="ISnapshotRefresh"/>. The
/// setters ask SDL for changes; a size or mode change shows in the snapshot on the refresh after SDL applies it (the
/// event pump refreshes on every one of the window's events).
/// </para>
/// <para>
/// <b>Threads:</b> the thread that creates the window owns it. Every member, <see cref="Dispose"/> included, runs on
/// that thread and throws <see cref="WrongThreadException"/> on any other, checked before disposal.
/// <see cref="IsDisposed"/> is the exception: the window's surface source reads it from any thread.
/// </para>
/// <para>
/// <b>Allocation:</b> creation allocates (the window, its surface source, error text). The getters, a successful
/// <see cref="RefreshSnapshot"/> and the setters that succeed allocate nothing, except
/// <see cref="SetExclusiveFullscreen"/>, which copies the display's mode list; a failure allocates its message.
/// </para>
/// <para>
/// <b>Ownership:</b> the creator owns the window and disposes it before the video session. Any Vulkan surface made from
/// <see cref="VulkanSurface"/> is destroyed before the window.
/// </para>
/// </remarks>
internal sealed class SdlPlatformWindow : IWindow, SdlVulkanSurfaceSource.IOwner, SdlPlatformWindow.ISnapshotRefresh
{
    /// <summary>
    /// A window whose snapshot the event pump refreshes from the window's events, as
    /// <see cref="SdlEventPump.IWindowLookup"/> finds it. <see cref="SdlPlatformWindow"/> implements it; a test fake can
    /// fail a refresh and count refreshes.
    /// </summary>
    internal interface ISnapshotRefresh
    {
        /// <summary>Re-reads the window's snapshot from the OS.</summary>
        /// <returns>Success, or the failure, which keeps the previous snapshot whole.</returns>
        /// <remarks>Called on the pumping thread. Allocates nothing on success.</remarks>
        public Result RefreshSnapshot();
    }

    /// <summary>The message for a Vulkan window on <see cref="HeadlessMode.NoSurface"/>, with <see cref="ErrorKind.Unsupported"/>.</summary>
    public const string NoSurfaceVulkanMessage =
        "HeadlessMode.NoSurface uses SDL's dummy video driver, which has no Vulkan. Create the window with WindowSurface.None.";

    private readonly SdlWindow _handle;
    private readonly ThreadAffinity _owner;
    private WindowId _id;
    private SdlVulkanSurfaceSource? _vulkanSurface;

    // The snapshot: plain fields, so the getters make no native call. Written only on the owner thread.
    private WindowSize _size;
    private WindowSize _pixelSize;
    private WindowSize _contentSize;
    private float _pixelDensity;
    private float _displayScale;
    private WindowState _state;
    private WindowMode _mode;
    private DisplayId _display;
    private bool _isTextInputActive;
    private bool _isRelativeMouseMode;

    // The content size to apply when the window next leaves fullscreen, or null. Set when the window is created
    // fullscreen (creation skips the content-size correction there) and by SetContentSize while it is fullscreen.
    private WindowSize? _windowedContentSize;

    // 1 once disposed. Written on the owner thread; the surface source reads it from any thread.
    private int _disposed;

    private SdlPlatformWindow(SdlWindow handle, ThreadAffinity owner)
    {
        _handle = handle;
        _owner = owner;
    }

    /// <inheritdoc cref="IWindow.Id"/>
    public WindowId Id => Read(_id, "IWindow.Id");

    /// <inheritdoc cref="IWindow.Size"/>
    public WindowSize Size => Read(_size, "IWindow.Size");

    /// <inheritdoc cref="IWindow.PixelSize"/>
    public WindowSize PixelSize => Read(_pixelSize, "IWindow.PixelSize");

    /// <inheritdoc cref="IWindow.ContentSize"/>
    public WindowSize ContentSize => Read(_contentSize, "IWindow.ContentSize");

    /// <inheritdoc cref="IWindow.PixelDensity"/>
    public float PixelDensity => Read(_pixelDensity, "IWindow.PixelDensity");

    /// <inheritdoc cref="IWindow.DisplayScale"/>
    public float DisplayScale => Read(_displayScale, "IWindow.DisplayScale");

    /// <inheritdoc cref="IWindow.State"/>
    public WindowState State => Read(_state, "IWindow.State");

    /// <inheritdoc cref="IWindow.Mode"/>
    public WindowMode Mode => Read(_mode, "IWindow.Mode");

    /// <inheritdoc cref="IWindow.Display"/>
    public DisplayId Display => Read(_display, "IWindow.Display");

    /// <inheritdoc cref="IWindow.IsTextInputActive"/>
    public bool IsTextInputActive => Read(_isTextInputActive, "IWindow.IsTextInputActive");

    /// <inheritdoc cref="IWindow.IsRelativeMouseMode"/>
    public bool IsRelativeMouseMode => Read(_isRelativeMouseMode, "IWindow.IsRelativeMouseMode");

    /// <inheritdoc cref="IWindow.VulkanSurface"/>
    public SdlVulkanSurfaceSource? VulkanSurface => Read(_vulkanSurface, "IWindow.VulkanSurface");

    /// <inheritdoc/>
    IVulkanSurfaceSource? IWindow.VulkanSurface => VulkanSurface;

    /// <summary>Whether the window has been disposed; once true, it stays true. Any thread, no allocation.</summary>
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>
    /// Creates a window on the calling thread, which becomes its owner thread: validates the options, applies the
    /// headless rules, creates the window on its display and sizes it in content units.
    /// </summary>
    /// <param name="options">The window's options.</param>
    /// <param name="videoDriver">The video driver in use, from the session.</param>
    /// <param name="headless">The headless mode the driver gives, from the session.</param>
    /// <param name="estimateScale">
    /// A test seam: replaces the display's content scale in the size estimate, so a test can force a wrong estimate and
    /// see it corrected. <see langword="null"/> (the default) reads it from SDL.
    /// </param>
    /// <returns>
    /// The window, or a failure: <see cref="ErrorKind.Unsupported"/> for a Vulkan window on
    /// <see cref="HeadlessMode.NoSurface"/> (<see cref="NoSurfaceVulkanMessage"/>, before any SDL call) or on
    /// <c>offscreen</c> without <c>VK_EXT_headless_surface</c> (<see cref="HeadlessSurfaceRule.Message"/>);
    /// <see cref="ErrorKind.NotFound"/> when <see cref="WindowOptions.Display"/> names no connected display; or
    /// <see cref="ErrorKind.Native"/> with SDL's message. A failure leaves no SDL window behind.
    /// </returns>
    /// <remarks>
    /// The size is estimated as content units times the display's content scale, which is already in window
    /// coordinates (so not divided by pixel density). Once the window exists, its own display scale and pixel density
    /// give the exact size, and a windowed window whose estimate was off is resized once. Window systems that apply
    /// resizes asynchronously (X11, Wayland) are waited for with <c>SDL_SyncWindow</c> before the snapshot is re-read.
    /// A sync that times out doesn't fail creation: the window is kept and its snapshot holds the size SDL reports at
    /// that point, until the event pump refreshes it from the resize event. Allocates.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> or <paramref name="videoDriver"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An option breaks its rule (see <see cref="WindowOptionsValidator"/>).</exception>
    /// <exception cref="ArgumentOutOfRangeException">A content size side or an enum option is out of range.</exception>
    internal static Result<SdlPlatformWindow> Create(WindowOptions options, string videoDriver, HeadlessMode headless, float? estimateScale = null)
    {
        WindowOptionsValidator.Validate(options);
        ArgumentNullException.ThrowIfNull(videoDriver);
        if (options.Surface == WindowSurface.Vulkan && headless == HeadlessMode.NoSurface)
        {
            // The dummy driver refuses SDL_WINDOW_VULKAN, so fail before calling SDL (section 3).
            return new EngineError(ErrorKind.Unsupported, NoSurfaceVulkanMessage);
        }

        Result<uint> display = ResolveDisplay(options.Display);
        if (!display.TryGetValue(out uint displayId))
        {
            return display.Error;
        }

        // SDL returns 0.0 on failure.
        float contentScale = estimateScale ?? Sdl.PInvoke.GetDisplayContentScale(displayId);
        if (!(contentScale > 0))
        {
            return SdlCheck.Error("SDL_GetDisplayContentScale");
        }

        // The display's content scale is already in window coordinates: the estimate isn't divided by pixel density.
        WindowSize estimate = ContentSizeModel.ToWindowCoordinates(options.ContentSize, contentScale, 1f);
        Result<SdlWindow> created = CreateNative(options, displayId, estimate);
        if (!created.TryGetValue(out SdlWindow handle))
        {
            return created.Error;
        }

        SdlPlatformWindow window = new(handle, ThreadAffinity.CurrentThread())
        {
            _windowedContentSize = options.Mode == WindowMode.Windowed ? null : options.ContentSize,
        };
        bool keep = false;
        try
        {
            Result initialized = window.Initialize(options, videoDriver, estimate);
            keep = initialized.IsSuccess;
            return keep ? window : initialized.Error;
        }
        finally
        {
            // A failed or throwing initialisation destroys the window, so a failure leaves no SDL window behind.
            if (!keep)
            {
                window.Dispose();
            }
        }
    }

    /// <summary>Sets the window's title through <c>SDL_SetWindowTitle</c>.</summary>
    /// <param name="title">The new title.</param>
    /// <returns>Success, or <see cref="ErrorKind.Native"/> with SDL's message if SDL refused.</returns>
    /// <remarks>
    /// Owner thread only. Allocates no managed memory on success: BRUTAL converts the title to UTF-8 in a stack buffer,
    /// or in native memory freed after the call when the title is long. A failure allocates its message.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="title"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">The window has been disposed.</exception>
    /// <exception cref="WrongThreadException">Called from a thread other than the owner thread.</exception>
    public Result SetTitle(string title)
    {
        ThrowIfUnusable("IWindow.SetTitle");
        ArgumentNullException.ThrowIfNull(title);
        return SdlCheck.Check(Sdl.PInvoke.SetWindowTitle(_handle, title), "SDL_SetWindowTitle");
    }

    /// <summary>
    /// Asks SDL for a new size in content units: converted to window coordinates with the snapshot's display scale and
    /// pixel density, as at creation, then set through <c>SDL_SetWindowSize</c>.
    /// </summary>
    /// <param name="contentSize">The requested size in content units. Each side must be from 1 to 16384.</param>
    /// <returns>Success, or <see cref="ErrorKind.Native"/> with SDL's message if SDL refused.</returns>
    /// <remarks>
    /// The snapshot (<see cref="Size"/>, <see cref="PixelSize"/>, <see cref="ContentSize"/>) changes on the
    /// <see cref="RefreshSnapshot"/> after SDL applies the size (the event pump refreshes on the resize event), because
    /// SDL can apply it asynchronously: on X11 and Wayland a refresh straight after this call can still read the old
    /// size. Nothing waits for it here. The result can differ from the request by rounding or because the OS clamped
    /// it. On a fullscreen window the size is also kept for <see cref="SetMode"/>, which applies it when the window
    /// returns to windowed. Owner thread only. Allocates nothing on success; a failure allocates its message.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">A side of <paramref name="contentSize"/> is outside 1 to 16384.</exception>
    /// <exception cref="ObjectDisposedException">The window has been disposed.</exception>
    /// <exception cref="WrongThreadException">Called from a thread other than the owner thread.</exception>
    public Result SetContentSize(WindowSize contentSize)
    {
        ThrowIfUnusable("IWindow.SetContentSize");
        WindowOptionsValidator.ValidateContentSize(contentSize, nameof(contentSize), nameof(contentSize));
        WindowSize size = ExactWindowSize(contentSize);
        Result set = SdlCheck.Check(Sdl.PInvoke.SetWindowSize(_handle, size.Width, size.Height), "SDL_SetWindowSize");
        if (set.IsSuccess)
        {
            // SDL doesn't keep a size set while fullscreen as the windowed size (observed on its dummy driver). On a
            // windowed window the size applies now, so null drops any pending size still left: one left over when
            // LeaveFullscreen's resize failed, or when the OS took the window out of fullscreen without SetMode.
            _windowedContentSize = IsFullscreen() ? contentSize : null;
        }

        return set;
    }

    /// <summary>
    /// Switches the window to windowed or borderless fullscreen through <c>SDL_SetWindowFullscreen</c>. Borderless
    /// first clears any exclusive mode with <c>SDL_SetWindowFullscreenMode(NULL)</c>.
    /// </summary>
    /// <param name="mode"><see cref="WindowMode.Windowed"/> or <see cref="WindowMode.BorderlessFullscreen"/>.</param>
    /// <returns>Success, or <see cref="ErrorKind.Native"/> with SDL's message if SDL refused.</returns>
    /// <remarks>
    /// <para>
    /// Leaving fullscreen applies the content-size model when a content size is waiting for it: the size the window was
    /// created with, if it was created fullscreen (creation corrects only windowed windows), or one set by
    /// <see cref="SetContentSize"/> while fullscreen. The window is synced with <c>SDL_SyncWindow</c>, resized once to
    /// that size in window coordinates and synced again; a sync that times out isn't a failure, as at creation.
    /// </para>
    /// <para>
    /// <see cref="Mode"/> and the sizes change on the refresh after SDL applies the switch. Owner thread only. Allocates
    /// nothing on success; a failure allocates its message.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="mode"/> is <see cref="WindowMode.ExclusiveFullscreen"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="mode"/> isn't a <see cref="WindowMode"/> value.</exception>
    /// <exception cref="ObjectDisposedException">The window has been disposed.</exception>
    /// <exception cref="WrongThreadException">Called from a thread other than the owner thread.</exception>
    public unsafe Result SetMode(WindowMode mode)
    {
        ThrowIfUnusable("IWindow.SetMode");
        switch (mode)
        {
            case WindowMode.Windowed:
                return LeaveFullscreen();

            case WindowMode.BorderlessFullscreen:
                // Sound: NULL is SDL's documented value for borderless (desktop) fullscreen.
                Result cleared = SdlCheck.Check(Sdl.PInvoke.SetWindowFullscreenMode(_handle, null), "SDL_SetWindowFullscreenMode");
                return cleared.IsFailure ? cleared : SdlCheck.Check(Sdl.PInvoke.SetWindowFullscreen(_handle, 1), "SDL_SetWindowFullscreen");

            case WindowMode.ExclusiveFullscreen:
                throw new ArgumentException("Exclusive fullscreen needs a display mode: use SetExclusiveFullscreen.", nameof(mode));

            default:
                throw new ArgumentOutOfRangeException(nameof(mode), mode, "Not a WindowMode value.");
        }
    }

    /// <summary>
    /// Switches the window to exclusive fullscreen in <paramref name="mode"/>: re-reads the display's fullscreen modes,
    /// takes the first exact match (<see cref="FullscreenModeMatcher"/>), then calls <c>SDL_SetWindowFullscreenMode</c>
    /// and <c>SDL_SetWindowFullscreen</c>.
    /// </summary>
    /// <param name="mode">A mode from the display's fullscreen-mode list.</param>
    /// <returns>
    /// Success; <see cref="ErrorKind.NotFound"/> if the display isn't connected or no mode of it matches (a display's
    /// current mode needn't be in its fullscreen list, and SDL's <c>dummy</c> and <c>offscreen</c> drivers list none);
    /// or <see cref="ErrorKind.Native"/> with SDL's message if SDL refused.
    /// </returns>
    /// <remarks>
    /// <see cref="Mode"/> changes on the refresh after SDL applies the switch. Owner thread only. Allocates a copy of the
    /// display's mode list, which a mode switch, a rare call, can afford; a failure also allocates its message.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="mode"/> has display <c>0</c>, as <c>default(DisplayMode)</c> does.</exception>
    /// <exception cref="ObjectDisposedException">The window has been disposed.</exception>
    /// <exception cref="WrongThreadException">Called from a thread other than the owner thread.</exception>
    public unsafe Result SetExclusiveFullscreen(DisplayMode mode)
    {
        ThrowIfUnusable("IWindow.SetExclusiveFullscreen");
        if (mode.Display == default)
        {
            throw new ArgumentException("The mode has display 0, as default(DisplayMode) does: pass a mode from a display's fullscreen-mode list.", nameof(mode));
        }

        Result<SdlDisplayMode[]> listed = SdlDisplays.CopyFullscreenModes(mode.Display);
        if (!listed.TryGetValue(out SdlDisplayMode[]? modes))
        {
            return listed.Error;
        }

        int index = FullscreenModeMatcher.FindExact(modes, mode);
        if (index < 0)
        {
            return new EngineError(
                ErrorKind.NotFound,
                string.Create(CultureInfo.InvariantCulture, $"{mode} matches no fullscreen mode of display {mode.Display.Value}: pick one from its fullscreen-mode list."));
        }

        // Sound: the array element is pinned for the call, and SDL copies the mode it is given.
        fixed (SdlDisplayMode* chosen = &modes[index])
        {
            Result set = SdlCheck.Check(Sdl.PInvoke.SetWindowFullscreenMode(_handle, chosen), "SDL_SetWindowFullscreenMode");
            if (set.IsFailure)
            {
                return set;
            }
        }

        return SdlCheck.Check(Sdl.PInvoke.SetWindowFullscreen(_handle, 1), "SDL_SetWindowFullscreen");
    }

    /// <summary>Turns text input on for this window through <c>SDL_StartTextInput</c>.</summary>
    /// <returns>Success, or <see cref="ErrorKind.Native"/> with SDL's message if SDL refused.</returns>
    /// <remarks>
    /// On success <see cref="IsTextInputActive"/> becomes <see langword="true"/> at once, since text input has no OS
    /// event; a failure leaves it unchanged. Owner thread only. Allocates nothing on success; a failure allocates its
    /// message.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The window has been disposed.</exception>
    /// <exception cref="WrongThreadException">Called from a thread other than the owner thread.</exception>
    public Result StartTextInput()
    {
        ThrowIfUnusable("IWindow.StartTextInput");
        Result started = SdlCheck.Check(Sdl.PInvoke.StartTextInput(_handle), "SDL_StartTextInput");
        if (started.IsSuccess)
        {
            _isTextInputActive = true;
        }

        return started;
    }

    /// <summary>Turns text input off for this window through <c>SDL_StopTextInput</c>.</summary>
    /// <returns>Success, or <see cref="ErrorKind.Native"/> with SDL's message if SDL refused.</returns>
    /// <remarks>
    /// On success <see cref="IsTextInputActive"/> becomes <see langword="false"/> at once; a failure leaves it unchanged.
    /// Owner thread only. Allocates nothing on success; a failure allocates its message.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The window has been disposed.</exception>
    /// <exception cref="WrongThreadException">Called from a thread other than the owner thread.</exception>
    public Result StopTextInput()
    {
        ThrowIfUnusable("IWindow.StopTextInput");
        Result stopped = SdlCheck.Check(Sdl.PInvoke.StopTextInput(_handle), "SDL_StopTextInput");
        if (stopped.IsSuccess)
        {
            _isTextInputActive = false;
        }

        return stopped;
    }

    /// <summary>Turns relative mouse mode on or off for this window through <c>SDL_SetWindowRelativeMouseMode</c>.</summary>
    /// <param name="enabled"><see langword="true"/> to hide and confine the cursor and report motion as deltas.</param>
    /// <returns>
    /// Success, or <see cref="ErrorKind.Native"/> with SDL's message if SDL refused (SDL's <c>offscreen</c> driver
    /// refuses it: "That operation is not supported").
    /// </returns>
    /// <remarks>
    /// On success <see cref="IsRelativeMouseMode"/> becomes <paramref name="enabled"/> at once, since the mode has no OS
    /// event; a failure leaves it unchanged. Owner thread only. Allocates nothing on success; a failure allocates its
    /// message.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The window has been disposed.</exception>
    /// <exception cref="WrongThreadException">Called from a thread other than the owner thread.</exception>
    public Result SetRelativeMouseMode(bool enabled)
    {
        ThrowIfUnusable("IWindow.SetRelativeMouseMode");
        Result set = SdlCheck.Check(Sdl.PInvoke.SetWindowRelativeMouseMode(_handle, enabled ? (byte)1 : (byte)0), "SDL_SetWindowRelativeMouseMode");
        if (set.IsSuccess)
        {
            _isRelativeMouseMode = enabled;
        }

        return set;
    }

    /// <summary>
    /// Re-reads the whole snapshot from SDL: sizes, pixel density, display scale, state, mode, display, text input and
    /// relative mouse mode. The event pump calls it on window events.
    /// </summary>
    /// <returns>
    /// Success, or <see cref="ErrorKind.Native"/> with SDL's message if a query failed; the previous snapshot is then
    /// kept whole, and so is the pending windowed content size.
    /// </returns>
    /// <remarks>
    /// A refresh that finds the window windowed drops the content size kept for <see cref="SetMode"/>'s return to
    /// windowed (the one the window was created fullscreen with, or one set while fullscreen), without applying it: the
    /// OS can leave fullscreen without <see cref="SetMode"/>, and a later fullscreen round trip would then apply a
    /// stale size. Owner thread only. Allocates nothing on success.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The window has been disposed.</exception>
    /// <exception cref="WrongThreadException">Called from a thread other than the owner thread.</exception>
    internal Result RefreshSnapshot()
    {
        ThrowIfUnusable("SdlPlatformWindow.RefreshSnapshot");
        Result read = ReadSnapshot();
        if (read.IsSuccess && _mode == WindowMode.Windowed)
        {
            // Windowed now, whether through SetMode or because the OS took the window out of fullscreen by itself (a
            // window-manager shortcut). A pending size was meant for SetMode's return to windowed: kept, a later
            // fullscreen round trip would apply it over the size the window has now, so it is dropped, not applied.
            _windowedContentSize = null;
        }

        return read;
    }

    /// <inheritdoc/>
    Result ISnapshotRefresh.RefreshSnapshot() => RefreshSnapshot();

    /// <summary>Destroys the SDL window, once; later calls do nothing.</summary>
    /// <remarks>
    /// Marks the window disposed before destroying it, so its surface source refuses to use the handle from then on.
    /// Owner thread only, checked even when already disposed.
    /// </remarks>
    /// <exception cref="WrongThreadException">Called from a thread other than the owner thread.</exception>
    public void Dispose()
    {
        _owner.ThrowIfNotCurrentThread("IWindow.Dispose");
        if (IsDisposed)
        {
            return;
        }

        Volatile.Write(ref _disposed, 1);
        Sdl.PInvoke.DestroyWindow(_handle);
    }

    /// <summary>Maps SDL's window flags to <see cref="WindowState"/>.</summary>
    /// <param name="flags">The flags from <c>SDL_GetWindowFlags</c>.</param>
    /// <returns><see cref="WindowState.Visible"/> unless hidden, plus each state flag SDL sets.</returns>
    internal static WindowState StateFrom(SdlWindowFlags flags)
    {
        WindowState state = (flags & SdlWindowFlags.Hidden) == 0 ? WindowState.Visible : WindowState.None;
        state |= (flags & SdlWindowFlags.Minimized) != 0 ? WindowState.Minimized : WindowState.None;
        state |= (flags & SdlWindowFlags.Maximized) != 0 ? WindowState.Maximized : WindowState.None;
        state |= (flags & SdlWindowFlags.InputFocus) != 0 ? WindowState.InputFocus : WindowState.None;
        state |= (flags & SdlWindowFlags.MouseFocus) != 0 ? WindowState.MouseFocus : WindowState.None;
        state |= (flags & SdlWindowFlags.Occluded) != 0 ? WindowState.Occluded : WindowState.None;
        return state;
    }

    private static Result<uint> ResolveDisplay(DisplayId? requested)
    {
        if (requested is not { } display)
        {
            uint primary = Sdl.PInvoke.GetPrimaryDisplay();
            return primary != 0 ? primary : SdlCheck.Error("SDL_GetPrimaryDisplay");
        }

        Result connected = SdlDisplays.RequireConnected(display.Value);
        return connected.IsSuccess ? display.Value : connected.Error;
    }

    private static Result<SdlWindow> CreateNative(WindowOptions options, uint display, WindowSize size)
    {
        SdlWindowFlags flags = (options.Resizable ? SdlWindowFlags.Resizable : 0)
            | (options.HighPixelDensity ? SdlWindowFlags.HighPixelDensity : 0)
            | (options.Surface == WindowSurface.Vulkan ? SdlWindowFlags.Vulkan : 0)
            | (options.Mode == WindowMode.BorderlessFullscreen ? SdlWindowFlags.Fullscreen : 0);

        // Centred on the target display: SDL_WINDOWPOS_CENTERED_DISPLAY(id) is the centred mask ORed with the id.
        long position = Sdl.Constants.WindowposCenteredMask | display;
        uint properties = Sdl.PInvoke.CreateProperties();
        if (properties == 0)
        {
            return SdlCheck.Error("SDL_CreateProperties");
        }

        try
        {
            bool set = Sdl.PInvoke.SetStringProperty(properties, Sdl.Constants.PropWindowCreateTitleString, options.Title) != 0
                && SetNumber(properties, Sdl.Constants.PropWindowCreateXNumber, position)
                && SetNumber(properties, Sdl.Constants.PropWindowCreateYNumber, position)
                && SetNumber(properties, Sdl.Constants.PropWindowCreateWidthNumber, size.Width)
                && SetNumber(properties, Sdl.Constants.PropWindowCreateHeightNumber, size.Height)
                && SetNumber(properties, Sdl.Constants.PropWindowCreateFlagsNumber, (long)flags);
            if (!set)
            {
                return SdlCheck.Error("SDL_SetStringProperty or SDL_SetNumberProperty");
            }

            SdlWindow handle = Sdl.PInvoke.CreateWindowWithProperties(properties);
            return handle.IsNull() ? SdlCheck.Error("SDL_CreateWindowWithProperties") : handle;
        }
        finally
        {
            Sdl.PInvoke.DestroyProperties(properties);
        }
    }

    private static bool SetNumber(uint properties, string name, long value) => Sdl.PInvoke.SetNumberProperty(properties, name, value) != 0;

    private Result Initialize(WindowOptions options, string videoDriver, WindowSize estimate)
    {
        uint id = Sdl.PInvoke.GetWindowID(_handle);
        if (id == 0)
        {
            return SdlCheck.Error("SDL_GetWindowID");
        }

        _id = new WindowId(id);
        if (options.Surface == WindowSurface.Vulkan)
        {
            Result<SdlVulkanSurfaceSource> source = SdlVulkanSurfaceSource.Create(_handle, _owner, this);
            if (!source.TryGetValue(out SdlVulkanSurfaceSource? surface))
            {
                return source.Error;
            }

            if (HeadlessSurfaceRule.RequiresHeadlessExtension(surface, videoDriver))
            {
                return new EngineError(ErrorKind.Unsupported, HeadlessSurfaceRule.Message);
            }

            _vulkanSurface = surface;
        }

        Result snapshot = ReadSnapshot();
        if (snapshot.IsFailure || options.Mode != WindowMode.Windowed)
        {
            // A fullscreen window takes its display's size, so there is nothing to correct.
            return snapshot;
        }

        // The window's own scale and density give the exact size; correct the estimate once if it was off.
        WindowSize exact = ExactWindowSize(options.ContentSize);
        if (exact == estimate)
        {
            return snapshot;
        }

        Result resized = SdlCheck.Check(Sdl.PInvoke.SetWindowSize(_handle, exact.Width, exact.Height), "SDL_SetWindowSize");
        if (resized.IsFailure)
        {
            return resized;
        }

        // SDL applies the resize asynchronously on some window systems, so wait for it before re-reading. SDL_SyncWindow
        // returns false only when it timed out (it returns within finite time, and at once where changes are immediate);
        // that isn't a failure, so its status is deliberately not checked: the snapshot then holds what SDL reports now.
        _ = Sdl.PInvoke.SyncWindow(_handle);
        return ReadSnapshot();
    }

    private unsafe Result ReadSnapshot()
    {
        int width, height, pixelWidth, pixelHeight;

        // Sound: the four ints are stack locals that outlive the calls, and the window is alive (the callers run on the
        // owner thread, the only one that destroys it, before or without disposal).
        if (Sdl.PInvoke.GetWindowSize(_handle, &width, &height) == 0)
        {
            return SdlCheck.Error("SDL_GetWindowSize");
        }

        if (Sdl.PInvoke.GetWindowSizeInPixels(_handle, &pixelWidth, &pixelHeight) == 0)
        {
            return SdlCheck.Error("SDL_GetWindowSizeInPixels");
        }

        // SDL returns 0.0 for both on failure.
        float pixelDensity = Sdl.PInvoke.GetWindowPixelDensity(_handle);
        if (!(pixelDensity > 0))
        {
            return SdlCheck.Error("SDL_GetWindowPixelDensity");
        }

        float displayScale = Sdl.PInvoke.GetWindowDisplayScale(_handle);
        if (!(displayScale > 0))
        {
            return SdlCheck.Error("SDL_GetWindowDisplayScale");
        }

        uint display = Sdl.PInvoke.GetDisplayForWindow(_handle);
        if (display == 0)
        {
            return SdlCheck.Error("SDL_GetDisplayForWindow");
        }

        SdlWindowFlags flags = Sdl.PInvoke.GetWindowFlags(_handle);

        // Every query succeeded: commit them together, so a failure above leaves the previous snapshot whole.
        _size = new WindowSize(width, height);
        _pixelSize = new WindowSize(pixelWidth, pixelHeight);
        _pixelDensity = pixelDensity;
        _displayScale = displayScale;
        _contentSize = ContentSizeModel.ContentFromPixels(_pixelSize, displayScale);
        _state = StateFrom(flags);

        // A fullscreen window without a fullscreen display mode is borderless (desktop) fullscreen.
        _mode = (flags & SdlWindowFlags.Fullscreen) == 0 ? WindowMode.Windowed
            : Sdl.PInvoke.GetWindowFullscreenMode(_handle) is null ? WindowMode.BorderlessFullscreen
            : WindowMode.ExclusiveFullscreen;
        _display = new DisplayId(display);
        _isTextInputActive = Sdl.PInvoke.TextInputActive(_handle) != 0;
        _isRelativeMouseMode = Sdl.PInvoke.GetWindowRelativeMouseMode(_handle) != 0;
        return Result.Success;
    }

    // Leaves fullscreen and, when a content size is waiting for it, applies it once the window is windowed again.
    private Result LeaveFullscreen()
    {
        Result left = SdlCheck.Check(Sdl.PInvoke.SetWindowFullscreen(_handle, 0), "SDL_SetWindowFullscreen");
        if (left.IsFailure || _windowedContentSize is not { } content)
        {
            return left;
        }

        // SDL can leave fullscreen asynchronously, and a size set before it has would be lost: wait first. A sync that
        // timed out isn't a failure (see Initialize), so its status is deliberately not checked.
        _ = Sdl.PInvoke.SyncWindow(_handle);
        WindowSize exact = ExactWindowSize(content);
        Result resized = SdlCheck.Check(Sdl.PInvoke.SetWindowSize(_handle, exact.Width, exact.Height), "SDL_SetWindowSize");
        if (resized.IsFailure)
        {
            return resized;
        }

        _windowedContentSize = null;
        _ = Sdl.PInvoke.SyncWindow(_handle);
        return Result.Success;
    }

    private bool IsFullscreen() => (Sdl.PInvoke.GetWindowFlags(_handle) & SdlWindowFlags.Fullscreen) != 0;

    // Content units to window coordinates with the window's own display scale and pixel density, from the snapshot.
    // The one conversion for both creation's exact step and SetContentSize, so the two can't drift apart.
    private WindowSize ExactWindowSize(WindowSize content) => ContentSizeModel.ToWindowCoordinates(content, _displayScale, _pixelDensity);

    // Generic, so reading a value field neither boxes nor allocates. The field is read before the checks, which is
    // harmless: it has no side effect, and a failed check throws before the value is returned.
    private T Read<T>(T value, string apiName)
    {
        ThrowIfUnusable(apiName);
        return value;
    }

    private void ThrowIfUnusable(string apiName)
    {
        _owner.ThrowIfNotCurrentThread(apiName);
        ObjectDisposedException.ThrowIf(IsDisposed, this);
    }
}
