using System;
using System.Collections.Generic;
using System.Threading;
using BrutalEngine.Core;

namespace BrutalEngine.Platform;

/// <summary>
/// The root of the platform layer: the backend in use, the clock and paths, windows, the event pump, displays, the
/// clipboard, cursors and message boxes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Created by a backend, owned by the host.</b> A backend's factory creates the platform; the host (the runtime, a
/// sample or a test) disposes it after every window and after the graphics device. There is no global instance. A
/// backend may allow only one live platform per process (SDL's video subsystem is process-wide, so the SDL 3 backend
/// does), and creating a second one is then misuse (<see cref="InvalidOperationException"/>).
/// </para>
/// <para>
/// <b>Threads:</b> the main thread is the thread that created the platform. Every member is main thread only and
/// throws <see cref="WrongThreadException"/> on any other thread, except <see cref="BackendName"/>,
/// <see cref="VideoDriver"/>, <see cref="Headless"/>, <see cref="Clock"/> and <see cref="BaseDirectory"/>, which are
/// immutable and safe on any thread.
/// </para>
/// <para>
/// <b>Allocation:</b> in steady state <see cref="PumpEvents"/>, <see cref="Events"/>, <see cref="WaitForEvents"/> and
/// <see cref="Displays"/> allocate nothing; a pumped display event rebuilds <see cref="Displays"/>, which allocates.
/// <see cref="CreateWindow"/>, the clipboard, the first use of each cursor shape, message boxes and every failure (its
/// error message) allocate.
/// </para>
/// <para>
/// <b>Failures:</b> expected failures are <see cref="Result"/>s: the OS or driver refusing is
/// <see cref="ErrorKind.Native"/> with the backend's message, a display that no longer exists is
/// <see cref="ErrorKind.NotFound"/>, something this mode can't do is <see cref="ErrorKind.Unsupported"/> or
/// <see cref="ErrorKind.Unavailable"/>, and a user data directory that can't be created is <see cref="ErrorKind.Io"/>.
/// Misuse throws: invalid arguments, the wrong thread, or use after <see cref="IDisposable.Dispose"/>.
/// </para>
/// <para>
/// <b>Dispose</b> (main thread only) destroys every window the host didn't dispose, logging a warning for each, then
/// shuts the backend down. A second <c>Dispose</c> does nothing; every other member then throws
/// <see cref="ObjectDisposedException"/>. See docs/design/platform.md section 1, "The platform root".
/// </para>
/// </remarks>
public interface IPlatform : IDisposable
{
    /// <summary>The name of the backend that implements the platform, for example <c>SDL3</c>.</summary>
    /// <remarks>Immutable; any thread. Allocates nothing.</remarks>
    /// <exception cref="ObjectDisposedException">
    /// The platform has been disposed. This member throws it on any thread, not only on the main thread.
    /// </exception>
    public string BackendName { get; }

    /// <summary>
    /// The video driver really in use, for example <c>windows</c>, <c>x11</c>, <c>wayland</c>, <c>cocoa</c>,
    /// <c>offscreen</c> or <c>dummy</c>.
    /// </summary>
    /// <remarks>
    /// Read from the backend after it started, so an override from the environment shows here. Immutable; any thread.
    /// Allocates nothing.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">
    /// The platform has been disposed. This member throws it on any thread, not only on the main thread.
    /// </exception>
    public string VideoDriver { get; }

    /// <summary>The headless mode the platform runs in, derived from <see cref="VideoDriver"/>.</summary>
    /// <remarks>
    /// <c>offscreen</c> is <see cref="HeadlessMode.Offscreen"/>, <c>dummy</c> is <see cref="HeadlessMode.NoSurface"/>
    /// and any other driver is <see cref="HeadlessMode.None"/>. Immutable; any thread. Allocates nothing.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">
    /// The platform has been disposed. This member throws it on any thread, not only on the main thread.
    /// </exception>
    public HeadlessMode Headless { get; }

    /// <summary>The clock for frame timing and event timestamps: <c>PlatformOptions.Clock</c>.</summary>
    /// <remarks>
    /// Defaults to <see cref="TimeProvider.System"/>, which is monotonic, high-resolution and allocation-free; tests
    /// inject their own. Immutable; any thread. Allocates nothing.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">
    /// The platform has been disposed. This member throws it on any thread, not only on the main thread.
    /// </exception>
    public TimeProvider Clock { get; }

    /// <summary>The absolute directory that holds the application: an OS path, not a virtual file system path.</summary>
    /// <remarks>Read once when the platform is created. Immutable; any thread. Allocates nothing.</remarks>
    /// <exception cref="ObjectDisposedException">
    /// The platform has been disposed. This member throws it on any thread, not only on the main thread.
    /// </exception>
    public string BaseDirectory { get; }

    /// <summary>Gets the absolute directory for the user's saves, settings and logs: an OS path.</summary>
    /// <returns>
    /// The directory; or <see cref="ErrorKind.Io"/> with the backend's message if it can't be resolved or created.
    /// </returns>
    /// <remarks>
    /// <c>PlatformOptions.UserDataDirectory</c> replaces it when set. Otherwise it is resolved on the first call, not when
    /// the platform is created, because the backend creates the directory: a test or tool that never asks never
    /// creates a folder in the user's profile. Later calls return the same value. Main thread only. The first call and
    /// a failure allocate.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The platform has been disposed.</exception>
    /// <exception cref="WrongThreadException">Called from a thread other than the main thread.</exception>
    public Result<string> GetUserDataDirectory();

    /// <summary>Creates a window.</summary>
    /// <param name="options">The window's title, size in content units, mode, surface and display.</param>
    /// <returns>
    /// The window, which the caller owns and disposes before the platform; <see cref="ErrorKind.Native"/> if the OS or
    /// driver refused; <see cref="ErrorKind.Unsupported"/> if the requested surface can't work in this headless mode
    /// (a Vulkan surface under <see cref="HeadlessMode.NoSurface"/>, or under <see cref="HeadlessMode.Offscreen"/>
    /// without <c>VK_EXT_headless_surface</c>); or <see cref="ErrorKind.NotFound"/> if the requested display isn't
    /// connected.
    /// </returns>
    /// <remarks>Main thread only. Allocates.</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> or its title is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="options"/> asks for <see cref="WindowMode.ExclusiveFullscreen"/> (use
    /// <see cref="IWindow.SetExclusiveFullscreen"/>) or for display <c>default(DisplayId)</c>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A content-size side is outside 1 to 16384, or a value is outside its enum.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The platform has been disposed.</exception>
    /// <exception cref="WrongThreadException">Called from a thread other than the main thread.</exception>
    public Result<IWindow> CreateWindow(WindowOptions options);

    /// <summary>Collects the OS events that arrived since the last pump: once per frame, from the host.</summary>
    /// <returns>
    /// The new batch, also available as <see cref="Events"/> until the next pump. It and the spans and text it hands
    /// out are valid until the next <see cref="PumpEvents"/> or <see cref="IDisposable.Dispose"/>.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Each pump replaces the previous batch, then translates queued events in OS order while the buffer
    /// (<c>PlatformOptions.EventCapacity</c> and <c>TextCapacityBytes</c>) has room. An event that
    /// <see cref="WaitForEvents"/> received comes first. When the buffer fills up, the rest stay queued in the OS, in
    /// order, for the next pump (<see cref="PlatformEventBatch.HasDeferredEvents"/>): events are deferred, never dropped.
    /// The one lossy case is a single text event longer than the remaining text space, which is cut at a code-point
    /// boundary.
    /// </para>
    /// <para>
    /// Pumping also updates every window's snapshot, and rebuilds <see cref="Displays"/> when a display event arrives.
    /// Main thread only. Allocates nothing in steady state; a display event allocates the new display list.
    /// </para>
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The platform has been disposed.</exception>
    /// <exception cref="WrongThreadException">Called from a thread other than the main thread.</exception>
    public PlatformEventBatch PumpEvents();

    /// <summary>The batch of the last <see cref="PumpEvents"/>, so input and the host read the same events.</summary>
    /// <remarks>
    /// Empty before the first pump. Valid until the next pump or <see cref="IDisposable.Dispose"/>; reading it after that
    /// is a bug. <see cref="WaitForEvents"/> doesn't change it. Main thread only. Allocates nothing.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The platform has been disposed.</exception>
    /// <exception cref="WrongThreadException">Called from a thread other than the main thread.</exception>
    public PlatformEventBatch Events { get; }

    /// <summary>Blocks until an OS event arrives or the timeout passes, for hosts with nothing to draw.</summary>
    /// <param name="timeout">
    /// How long to wait: <see cref="TimeSpan.Zero"/> only checks, and <see cref="Timeout.InfiniteTimeSpan"/> waits without
    /// a limit.
    /// </param>
    /// <returns>
    /// <see langword="true"/> if an event arrived: it is translated and kept aside (with a copy of its text), and the next
    /// <see cref="PumpEvents"/> hands it out first. <see langword="false"/> if the timeout passed.
    /// </returns>
    /// <remarks>
    /// Hosts use it while every window is minimised. The current <see cref="Events"/> batch isn't touched. Main thread
    /// only. Allocates nothing.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="timeout"/> is negative (other than <see cref="Timeout.InfiniteTimeSpan"/>) or longer than
    /// <see cref="int.MaxValue"/> milliseconds.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The platform has been disposed.</exception>
    /// <exception cref="WrongThreadException">Called from a thread other than the main thread.</exception>
    public bool WaitForEvents(TimeSpan timeout);

    /// <summary>The connected displays, the primary one marked with <see cref="DisplayInfo.IsPrimary"/>.</summary>
    /// <remarks>
    /// A snapshot list, built when the platform is created and rebuilt (allocating) when a display is added or removed,
    /// or a display's content scale or current mode changes, is pumped. Between those, reading it allocates nothing and
    /// returns the same list. Main thread only.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The platform has been disposed.</exception>
    /// <exception cref="WrongThreadException">Called from a thread other than the main thread.</exception>
    public IReadOnlyList<DisplayInfo> Displays { get; }

    /// <summary>Gets the modes a display offers for exclusive fullscreen.</summary>
    /// <param name="display">The display, from <see cref="Displays"/>.</param>
    /// <returns>
    /// The modes, in the backend's order, to pass to <see cref="IWindow.SetExclusiveFullscreen"/>; or
    /// <see cref="ErrorKind.NotFound"/> if the display was never seen or has been unplugged since. A display's current
    /// mode need not be in the list.
    /// </returns>
    /// <remarks>Main thread only. Allocates the list.</remarks>
    /// <exception cref="ArgumentException"><paramref name="display"/> is <c>default(DisplayId)</c>.</exception>
    /// <exception cref="ObjectDisposedException">The platform has been disposed.</exception>
    /// <exception cref="WrongThreadException">Called from a thread other than the main thread.</exception>
    public Result<IReadOnlyList<DisplayMode>> GetFullscreenModes(DisplayId display);

    /// <summary>Gets the text on the OS clipboard.</summary>
    /// <returns>The text, empty if the clipboard holds none; or <see cref="ErrorKind.Native"/> if the OS refused.</returns>
    /// <remarks>Main thread only. Allocates the string.</remarks>
    /// <exception cref="ObjectDisposedException">The platform has been disposed.</exception>
    /// <exception cref="WrongThreadException">Called from a thread other than the main thread.</exception>
    public Result<string> GetClipboardText();

    /// <summary>Puts text on the OS clipboard.</summary>
    /// <param name="text">The text.</param>
    /// <returns>Success, or <see cref="ErrorKind.Native"/> if the OS refused.</returns>
    /// <remarks>Main thread only. Allocates to convert the text to UTF-8.</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">The platform has been disposed.</exception>
    /// <exception cref="WrongThreadException">Called from a thread other than the main thread.</exception>
    public Result SetClipboardText(string text);

    /// <summary>Sets the mouse cursor's shape.</summary>
    /// <param name="cursor">The shape.</param>
    /// <returns>Success, or <see cref="ErrorKind.Native"/> if the OS refused or has no such cursor.</returns>
    /// <remarks>
    /// Each shape is created on first use (which allocates) and kept until <see cref="IDisposable.Dispose"/>. Main
    /// thread only.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="cursor"/> isn't a <see cref="SystemCursor"/> value.</exception>
    /// <exception cref="ObjectDisposedException">The platform has been disposed.</exception>
    /// <exception cref="WrongThreadException">Called from a thread other than the main thread.</exception>
    public Result SetCursor(SystemCursor cursor);

    /// <summary>Shows or hides the mouse cursor.</summary>
    /// <param name="visible"><see langword="true"/> to show it.</param>
    /// <returns>Success, or <see cref="ErrorKind.Native"/> if the OS refused.</returns>
    /// <remarks>Main thread only. Allocates nothing on success.</remarks>
    /// <exception cref="ObjectDisposedException">The platform has been disposed.</exception>
    /// <exception cref="WrongThreadException">Called from a thread other than the main thread.</exception>
    public Result SetCursorVisible(bool visible);

    /// <summary>Shows a modal OS message box with an OK button, and blocks until it is dismissed.</summary>
    /// <param name="request">The kind, title, message and optional parent window.</param>
    /// <returns>
    /// Success once dismissed; <see cref="ErrorKind.Unavailable"/> at once, without showing anything, when the platform
    /// is headless, so automated runs never block on a dialog; or <see cref="ErrorKind.Native"/> if the OS refused.
    /// </returns>
    /// <remarks>Main thread only. Allocates.</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> or its title or message is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="request"/>'s kind isn't a <see cref="MessageBoxKind"/> value.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The platform has been disposed.</exception>
    /// <exception cref="WrongThreadException">Called from a thread other than the main thread.</exception>
    public Result ShowMessageBox(MessageBoxRequest request);
}
