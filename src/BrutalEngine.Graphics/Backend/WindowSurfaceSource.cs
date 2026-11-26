using System;
using BrutalEngine.Core;
using BrutalEngine.Platform;

namespace BrutalEngine.Graphics.Backend;

/// <summary>
/// A window's surface capability as a backend receives it: the window's Vulkan surface source plus the window's size and
/// minimised state, with only <see langword="int"/>, <see langword="bool"/>, <see langword="nint"/>, spans and
/// <see cref="Result{T}"/> in its surface, so a backend never names a Platform type (docs/design/graphics.md section 3).
/// </summary>
/// <remarks>
/// <para>
/// Only the graphics device creates one, per window it presents to, and keeps it while that window's swapchain lives.
/// It holds the window without owning it: nothing here disposes the window or the surface source.
/// </para>
/// <para>
/// The extension members and <see cref="CreateVulkanSurface"/> forward to the window's <see cref="IVulkanSurfaceSource"/>
/// unchanged, results and exceptions included, so they follow its rules: <see cref="RequiredInstanceExtensionCount"/> and
/// <see cref="GetRequiredInstanceExtension"/> run on any thread, <see cref="CreateVulkanSurface"/> on the main thread
/// only, and after the window is disposed each throws <see cref="ObjectDisposedException"/>.
/// </para>
/// <para>
/// <see cref="PixelWidth"/>, <see cref="PixelHeight"/> and <see cref="IsMinimised"/> are snapshots of
/// <see cref="IWindow.PixelSize"/> and <see cref="IWindow.State"/>: taken at creation, and refreshed by the device on the
/// main thread before every backend call that uses them. Read them on the thread that calls the backend. No member
/// allocates, except a failed <see cref="CreateVulkanSurface"/> (its error message).
/// </para>
/// </remarks>
public sealed class WindowSurfaceSource
{
    private readonly IWindow _window;
    private readonly IVulkanSurfaceSource _source;

    /// <summary>
    /// Wraps a window and its surface source, and takes the first size snapshot (<see cref="Refresh"/>). Only the
    /// graphics device creates one, on the main thread.
    /// </summary>
    /// <param name="window">The window to read the size and state from.</param>
    /// <param name="source">The window's surface source, <see cref="IWindow.VulkanSurface"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="window"/> or <paramref name="source"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">The window has been disposed.</exception>
    /// <exception cref="WrongThreadException">Called from a thread other than the main thread.</exception>
    internal WindowSurfaceSource(IWindow window, IVulkanSurfaceSource source)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(source);
        _window = window;
        _source = source;
        Refresh();
    }

    /// <summary>
    /// The number of instance extensions the Vulkan instance must enable to create this window's surface, from
    /// <see cref="IVulkanSurfaceSource.RequiredInstanceExtensionCount"/>.
    /// </summary>
    /// <remarks>Any thread. Allocates nothing.</remarks>
    /// <exception cref="ObjectDisposedException">The window has been disposed.</exception>
    public int RequiredInstanceExtensionCount => _source.RequiredInstanceExtensionCount;

    /// <summary>
    /// The width the backend uses for this window's swapchain, in pixels: <see cref="IWindow.PixelSize"/> at the last
    /// refresh. Can be 0 while the window is minimised.
    /// </summary>
    /// <remarks>A snapshot; no native call. Allocates nothing.</remarks>
    public int PixelWidth { get; private set; }

    /// <summary>
    /// The height the backend uses for this window's swapchain, in pixels: <see cref="IWindow.PixelSize"/> at the last
    /// refresh. Can be 0 while the window is minimised.
    /// </summary>
    /// <remarks>A snapshot; no native call. Allocates nothing.</remarks>
    public int PixelHeight { get; private set; }

    /// <summary>
    /// Whether the window can't be presented to at the last refresh: <see cref="WindowState.Minimized"/> was set, or a
    /// pixel side was 0. An acquire for such a window returns <see cref="AcquireStatus.Minimised"/> without recreating.
    /// </summary>
    /// <remarks>A snapshot; no native call. Allocates nothing.</remarks>
    public bool IsMinimised { get; private set; }

    /// <summary>
    /// Gets the name of one required instance extension, from
    /// <see cref="IVulkanSurfaceSource.GetRequiredInstanceExtension"/>.
    /// </summary>
    /// <param name="index">The extension's index, from 0 to <see cref="RequiredInstanceExtensionCount"/> minus 1.</param>
    /// <returns>
    /// The name as UTF-8, without a NUL terminator. It points into storage the window owns and stays valid while the
    /// window is alive.
    /// </returns>
    /// <remarks>Any thread. Allocates nothing.</remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is negative, or not below <see cref="RequiredInstanceExtensionCount"/>.</exception>
    /// <exception cref="ObjectDisposedException">The window has been disposed.</exception>
    public ReadOnlySpan<byte> GetRequiredInstanceExtension(int index) => _source.GetRequiredInstanceExtension(index);

    /// <summary>Creates the window's Vulkan surface (<c>VkSurfaceKHR</c>), through <see cref="IVulkanSurfaceSource.CreateVulkanSurface"/>.</summary>
    /// <param name="instance">The <c>VkInstance</c>, created with every required instance extension enabled.</param>
    /// <returns>
    /// The surface handle, or the source's failure unchanged (<see cref="ErrorKind.Native"/> with the platform's text).
    /// </returns>
    /// <remarks>
    /// Main thread only. The backend owns the surface and destroys it with <c>vkDestroySurfaceKHR</c> before the window
    /// is disposed; at most one surface is alive per window. Allocates only on failure.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="instance"/> is 0.</exception>
    /// <exception cref="ObjectDisposedException">The window has been disposed.</exception>
    /// <exception cref="WrongThreadException">Called from a thread other than the main thread.</exception>
    public Result<nint> CreateVulkanSurface(nint instance) => _source.CreateVulkanSurface(instance);

    /// <summary>
    /// Reads the window's pixel size and state snapshots into <see cref="PixelWidth"/>, <see cref="PixelHeight"/> and
    /// <see cref="IsMinimised"/>. The device calls it on the main thread before each backend call that uses them.
    /// </summary>
    /// <remarks>
    /// No native call (the window's getters are snapshots). Allocates nothing. If the window throws, the previous
    /// snapshot stays.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The window has been disposed.</exception>
    /// <exception cref="WrongThreadException">Called from a thread other than the main thread.</exception>
    internal void Refresh()
    {
        WindowSize size = _window.PixelSize;
        WindowState state = _window.State;
        PixelWidth = size.Width;
        PixelHeight = size.Height;
        IsMinimised = (state & WindowState.Minimized) != 0 || size.Width == 0 || size.Height == 0;
    }
}
