namespace BrutalEngine.Platform;

/// <summary>Whether a platform shows real windows, and whether its windows can have a graphics surface.</summary>
/// <remarks>
/// <para>
/// The host asks for a mode with <see cref="PlatformOptions.Headless"/>, and the backend picks its video driver from it
/// (SDL's <c>offscreen</c> driver for <see cref="Offscreen"/>, <c>dummy</c> for <see cref="NoSurface"/>). The driver in
/// use wins: an <c>SDL_VIDEO_DRIVER</c> environment variable overrides the request, and the platform reports the mode
/// it really got. If the host asked for a headless mode and the driver doesn't give it, creating the platform fails
/// with <see cref="BrutalEngine.Core.ErrorKind.Unsupported"/>.
/// </para>
/// <para>Headless platforms never show message boxes. The values are fixed: new modes are appended, never renumbered.</para>
/// </remarks>
public enum HeadlessMode
{
    /// <summary>Not headless: real windows on the desktop. The default.</summary>
    None = 0,

    /// <summary>
    /// Invisible windows that can have a Vulkan surface, which presents through <c>VK_EXT_headless_surface</c>. Where no
    /// installed Vulkan driver offers that extension (no Windows driver does today), creating a window with
    /// <see cref="WindowSurface.Vulkan"/> fails with <see cref="BrutalEngine.Core.ErrorKind.Unsupported"/>.
    /// </summary>
    Offscreen = 1,

    /// <summary>
    /// Invisible windows with no graphics surface, for platform tests and servers. Creating a window with
    /// <see cref="WindowSurface.Vulkan"/> fails with <see cref="BrutalEngine.Core.ErrorKind.Unsupported"/>.
    /// </summary>
    NoSurface = 2,
}
