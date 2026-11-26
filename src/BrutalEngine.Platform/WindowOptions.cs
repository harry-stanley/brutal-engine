using System;

namespace BrutalEngine.Platform;

/// <summary>Settings for a new window, passed to the platform's <c>CreateWindow</c>.</summary>
/// <remarks>
/// <para>
/// The record only describes the window; it doesn't check its values. The backend validates them when it creates the
/// window. A <see langword="null"/> options object throws <see cref="ArgumentNullException"/>. A
/// <see langword="null"/> <see cref="Title"/>, or a <see cref="Mode"/> of <see cref="WindowMode.ExclusiveFullscreen"/>,
/// throws <see cref="ArgumentException"/>. A <see cref="ContentSize"/> side outside its range, or an undefined enum
/// value, throws <see cref="ArgumentOutOfRangeException"/>. A <see cref="Display"/> that doesn't exist (it was unplugged
/// since it was listed) is an expected failure, <see cref="BrutalEngine.Core.ErrorKind.NotFound"/>, not an exception.
/// </para>
/// <para>
/// Equality compares every property. An immutable record: the window copies what it needs when it is created, so a
/// later <c>with</c> copy has no effect on it. Safe to share between threads. Creating one allocates; reading it
/// doesn't.
/// </para>
/// </remarks>
public sealed record WindowOptions
{
    /// <summary>
    /// The window's title, shown in its title bar. Default <c>"BrutalEngine"</c>. Must not be <see langword="null"/>.
    /// </summary>
    public string Title { get; init; } = "BrutalEngine";

    /// <summary>
    /// The size of the window's content in content units, where UI and gameplay lay out. Default 1280 by 720; each side
    /// is valid from 1 to 16384.
    /// </summary>
    /// <remarks>
    /// Content units make a window the same physical size on every OS at the same scale setting. The backend converts
    /// the request to window coordinates for the target display, so <see cref="IWindow.ContentSize"/> can differ from it
    /// by rounding, or because the OS clamped the window.
    /// </remarks>
    public WindowSize ContentSize { get; init; } = new(1280, 720);

    /// <summary>
    /// How the window occupies the screen when it opens: <see cref="WindowMode.Windowed"/> (the default) or
    /// <see cref="WindowMode.BorderlessFullscreen"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="WindowMode.ExclusiveFullscreen"/> is rejected here, because it needs a display mode: create the window,
    /// then call <see cref="IWindow.SetExclusiveFullscreen"/>.
    /// </remarks>
    public WindowMode Mode { get; init; } = WindowMode.Windowed;

    /// <summary>Whether the user can resize the window. Default <see langword="true"/>.</summary>
    public bool Resizable { get; init; } = true;

    /// <summary>
    /// Whether to ask for full-resolution pixels on high-DPI displays. Default <see langword="true"/>.
    /// </summary>
    /// <remarks>
    /// It matters where window coordinates are points (macOS and Wayland); on Windows and X11 they are already pixels.
    /// </remarks>
    public bool HighPixelDensity { get; init; } = true;

    /// <summary>
    /// The graphics surface the window is created for. Default <see cref="WindowSurface.Vulkan"/>: the window then has an
    /// <see cref="IWindow.VulkanSurface"/>.
    /// </summary>
    /// <remarks>
    /// A headless platform may not support the surface, and creation then fails with
    /// <see cref="BrutalEngine.Core.ErrorKind.Unsupported"/> (see <see cref="HeadlessMode"/>). Platform tests that need no
    /// graphics use <see cref="WindowSurface.None"/>.
    /// </remarks>
    public WindowSurface Surface { get; init; } = WindowSurface.Vulkan;

    /// <summary>The display to open the window on. Default <see langword="null"/>: the primary display.</summary>
    public DisplayId? Display { get; init; }
}
