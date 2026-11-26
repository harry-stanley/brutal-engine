using System;
using BrutalEngine.Core;

namespace BrutalEngine.Platform;

/// <summary>A window created by the platform: its sizes, state and mode, and the calls that change them.</summary>
/// <remarks>
/// <para>
/// A window owns a native resource with a strict destruction order: any graphics surface made from
/// <see cref="VulkanSurface"/> first, then the window, then the platform. Events refer to windows by <see cref="Id"/>.
/// </para>
/// <para>
/// <b>Getters are snapshots</b> taken when the window is created and updated only by the platform's event pump, so they
/// stay consistent for a whole frame, make no native call and allocate nothing. <b>Setters</b> ask the OS and return a
/// <see cref="Result"/>, because the OS may refuse (<see cref="ErrorKind.Native"/> with the backend's message). The
/// snapshot changes when the resulting event is pumped, since the OS applies some changes asynchronously. Setters
/// allocate nothing when they succeed, except where a member says otherwise; a failure allocates its error message.
/// </para>
/// <para>
/// <b>Sizes</b> come in three units (docs/design/platform.md, "Window coordinates, pixels and content units"):
/// <see cref="Size"/> in the OS's window coordinates, <see cref="PixelSize"/> in pixels, and <see cref="ContentSize"/>
/// in content units, where UI and gameplay lay out. Scale once, from pixels: render content at content units times
/// <see cref="DisplayScale"/> pixels. Multiplying <see cref="Size"/> by <see cref="DisplayScale"/> scales twice where
/// window coordinates are points (macOS, Wayland).
/// </para>
/// <para>
/// <b>Threads:</b> every member, <see cref="IDisposable.Dispose"/> included, is main thread only (the thread that created
/// the platform) and throws <see cref="WrongThreadException"/> on any other thread.
/// </para>
/// <para>
/// <b>Dispose</b> destroys the window. Destroy any surface created from <see cref="VulkanSurface"/> first. After
/// <c>Dispose</c>, every member throws <see cref="ObjectDisposedException"/>; a second <c>Dispose</c> does nothing.
/// </para>
/// </remarks>
public interface IWindow : IDisposable
{
    /// <summary>The window's id, the one its events carry. Never <c>default(WindowId)</c>.</summary>
    /// <remarks>Fixed for the window's lifetime. Main thread only. Allocates nothing.</remarks>
    /// <exception cref="ObjectDisposedException">The window has been disposed.</exception>
    /// <exception cref="WrongThreadException">Called from a thread other than the main thread.</exception>
    public WindowId Id { get; }

    /// <summary>
    /// The window's size in window coordinates, the space of mouse positions. Their unit is the OS's: pixels on Windows and
    /// X11, points on macOS and Wayland.
    /// </summary>
    /// <remarks>A snapshot, updated when a resize is pumped. Main thread only. Allocates nothing.</remarks>
    /// <exception cref="ObjectDisposedException">The window has been disposed.</exception>
    /// <exception cref="WrongThreadException">Called from a thread other than the main thread.</exception>
    public WindowSize Size { get; }

    /// <summary>The drawable size in pixels: what a swapchain uses. A minimised window can report 0 by 0.</summary>
    /// <remarks>A snapshot, updated when a pixel-size change is pumped. Main thread only. Allocates nothing.</remarks>
    /// <exception cref="ObjectDisposedException">The window has been disposed.</exception>
    /// <exception cref="WrongThreadException">Called from a thread other than the main thread.</exception>
    public WindowSize PixelSize { get; }

    /// <summary>
    /// The window's size in content units, where UI and gameplay lay out: <see cref="PixelSize"/> divided by
    /// <see cref="DisplayScale"/>, each side rounded to the nearest integer with midpoints away from zero
    /// (<see cref="MidpointRounding.AwayFromZero"/>).
    /// </summary>
    /// <remarks>
    /// For example 3840 by 2160 pixels at a display scale of 2.0 is 1920 by 1080 content units, on every OS. Recomputed
    /// after any size, pixel-size, display-scale or display change is pumped. Main thread only. Allocates nothing.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The window has been disposed.</exception>
    /// <exception cref="WrongThreadException">Called from a thread other than the main thread.</exception>
    public WindowSize ContentSize { get; }

    /// <summary>
    /// Pixels per window coordinate, <see cref="PixelSize"/> divided by <see cref="Size"/>: 1.0 on Windows at any scale
    /// setting, 2.0 on a Retina display with high pixel density.
    /// </summary>
    /// <remarks>
    /// Converts positions: pixels = window coordinates times this. A snapshot, re-read with <see cref="DisplayScale"/>.
    /// Main thread only. Allocates nothing.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The window has been disposed.</exception>
    /// <exception cref="WrongThreadException">Called from a thread other than the main thread.</exception>
    public float PixelDensity { get; }

    /// <summary>
    /// The content scale, relative to pixels: how many pixels one content unit takes (2.0 at 200 % on Windows, and on a
    /// Retina display with high pixel density).
    /// </summary>
    /// <remarks>
    /// Converts positions: content units = pixels divided by this. A snapshot, updated when a display-scale change is
    /// pumped. Main thread only. Allocates nothing.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The window has been disposed.</exception>
    /// <exception cref="WrongThreadException">Called from a thread other than the main thread.</exception>
    public float DisplayScale { get; }

    /// <summary>The window's state flags: visible, minimised, maximised, focused, occluded.</summary>
    /// <remarks>A snapshot, updated by the pump. Main thread only. Allocates nothing.</remarks>
    /// <exception cref="ObjectDisposedException">The window has been disposed.</exception>
    /// <exception cref="WrongThreadException">Called from a thread other than the main thread.</exception>
    public WindowState State { get; }

    /// <summary>How the window occupies the screen: windowed, borderless fullscreen or exclusive fullscreen.</summary>
    /// <remarks>
    /// A snapshot: after <see cref="SetMode"/> or <see cref="SetExclusiveFullscreen"/> succeeds, it changes when the
    /// resulting event is pumped. Main thread only. Allocates nothing.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The window has been disposed.</exception>
    /// <exception cref="WrongThreadException">Called from a thread other than the main thread.</exception>
    public WindowMode Mode { get; }

    /// <summary>The display the window is on.</summary>
    /// <remarks>A snapshot, updated when a move to another display is pumped. Main thread only. Allocates nothing.</remarks>
    /// <exception cref="ObjectDisposedException">The window has been disposed.</exception>
    /// <exception cref="WrongThreadException">Called from a thread other than the main thread.</exception>
    public DisplayId Display { get; }

    /// <summary>Whether text input is on for this window, so it receives committed text.</summary>
    /// <remarks>
    /// Set by a successful <see cref="StartTextInput"/> and cleared by a successful <see cref="StopTextInput"/>.
    /// Main thread only. Allocates nothing.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The window has been disposed.</exception>
    /// <exception cref="WrongThreadException">Called from a thread other than the main thread.</exception>
    public bool IsTextInputActive { get; }

    /// <summary>
    /// Whether relative mouse mode is on for this window: the cursor is hidden and confined, and motion reports deltas.
    /// </summary>
    /// <remarks>Changed by a successful <see cref="SetRelativeMouseMode"/>. Main thread only. Allocates nothing.</remarks>
    /// <exception cref="ObjectDisposedException">The window has been disposed.</exception>
    /// <exception cref="WrongThreadException">Called from a thread other than the main thread.</exception>
    public bool IsRelativeMouseMode { get; }

    /// <summary>
    /// The window's Vulkan surface source, or <see langword="null"/> unless the window was created with a Vulkan surface
    /// (<c>WindowSurface.Vulkan</c>).
    /// </summary>
    /// <remarks>
    /// The same object for the window's whole lifetime; it is disposed with the window. Main thread only. Allocates
    /// nothing.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The window has been disposed.</exception>
    /// <exception cref="WrongThreadException">Called from a thread other than the main thread.</exception>
    public IVulkanSurfaceSource? VulkanSurface { get; }

    /// <summary>Sets the window's title.</summary>
    /// <param name="title">The new title.</param>
    /// <returns>Success, or <see cref="ErrorKind.Native"/> if the OS refused.</returns>
    /// <remarks>Main thread only. May allocate to convert the title to UTF-8.</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="title"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">The window has been disposed.</exception>
    /// <exception cref="WrongThreadException">Called from a thread other than the main thread.</exception>
    public Result SetTitle(string title);

    /// <summary>Asks for a new size in content units, so the window keeps its physical size on every OS.</summary>
    /// <param name="contentSize">The requested size in content units. Each side must be from 1 to 16384.</param>
    /// <returns>Success, or <see cref="ErrorKind.Native"/> if the OS refused.</returns>
    /// <remarks>
    /// The backend converts to window coordinates (content units times <see cref="DisplayScale"/> divided by
    /// <see cref="PixelDensity"/>, rounded to whole window coordinates). <see cref="ContentSize"/> reports the result once
    /// it is pumped, and can differ from the request by rounding or because the OS clamped it. Main thread only.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">A side of <paramref name="contentSize"/> is outside 1 to 16384.</exception>
    /// <exception cref="ObjectDisposedException">The window has been disposed.</exception>
    /// <exception cref="WrongThreadException">Called from a thread other than the main thread.</exception>
    public Result SetContentSize(WindowSize contentSize);

    /// <summary>Switches the window to <see cref="WindowMode.Windowed"/> or <see cref="WindowMode.BorderlessFullscreen"/>.</summary>
    /// <param name="mode">The mode. Exclusive fullscreen needs a display mode: use <see cref="SetExclusiveFullscreen"/>.</param>
    /// <returns>Success, or <see cref="ErrorKind.Native"/> if the OS refused (for example, fullscreen without a real display).</returns>
    /// <remarks>Main thread only.</remarks>
    /// <exception cref="ArgumentException"><paramref name="mode"/> is <see cref="WindowMode.ExclusiveFullscreen"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="mode"/> isn't a <see cref="WindowMode"/> value.</exception>
    /// <exception cref="ObjectDisposedException">The window has been disposed.</exception>
    /// <exception cref="WrongThreadException">Called from a thread other than the main thread.</exception>
    public Result SetMode(WindowMode mode);

    /// <summary>Switches the window to exclusive fullscreen in one of its display's modes.</summary>
    /// <param name="mode">
    /// A mode the platform returned (its fullscreen-mode list for a display, or a display's current mode). It is matched
    /// exactly, field by field, against the display's current list; there is no closest match.
    /// </param>
    /// <returns>
    /// Success; <see cref="ErrorKind.NotFound"/> if the mode matches nothing (the display was unplugged, its modes changed,
    /// or the value was built by hand); or <see cref="ErrorKind.Native"/> if the OS refused.
    /// </returns>
    /// <remarks>Main thread only.</remarks>
    /// <exception cref="ArgumentException"><paramref name="mode"/> has display <c>0</c>, as <c>default(DisplayMode)</c> does.</exception>
    /// <exception cref="ObjectDisposedException">The window has been disposed.</exception>
    /// <exception cref="WrongThreadException">Called from a thread other than the main thread.</exception>
    public Result SetExclusiveFullscreen(DisplayMode mode);

    /// <summary>Turns text input on for this window, so committed text arrives as text-input events.</summary>
    /// <returns>Success, or <see cref="ErrorKind.Native"/> if the OS refused.</returns>
    /// <remarks>Only committed text is delivered: there is no IME composition UI. Main thread only.</remarks>
    /// <exception cref="ObjectDisposedException">The window has been disposed.</exception>
    /// <exception cref="WrongThreadException">Called from a thread other than the main thread.</exception>
    public Result StartTextInput();

    /// <summary>Turns text input off for this window.</summary>
    /// <returns>Success, or <see cref="ErrorKind.Native"/> if the OS refused.</returns>
    /// <remarks>Main thread only.</remarks>
    /// <exception cref="ObjectDisposedException">The window has been disposed.</exception>
    /// <exception cref="WrongThreadException">Called from a thread other than the main thread.</exception>
    public Result StopTextInput();

    /// <summary>Turns relative mouse mode on or off for this window.</summary>
    /// <param name="enabled"><see langword="true"/> to hide and confine the cursor and report motion as deltas.</param>
    /// <returns>Success, or <see cref="ErrorKind.Native"/> if the OS refused (for example, there is no mouse).</returns>
    /// <remarks>Main thread only.</remarks>
    /// <exception cref="ObjectDisposedException">The window has been disposed.</exception>
    /// <exception cref="WrongThreadException">Called from a thread other than the main thread.</exception>
    public Result SetRelativeMouseMode(bool enabled);
}
