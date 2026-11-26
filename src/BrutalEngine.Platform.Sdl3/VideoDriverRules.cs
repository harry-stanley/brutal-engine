using System;

namespace BrutalEngine.Platform.Sdl3;

/// <summary>
/// The pure rules that connect headless modes and SDL video drivers (docs/design/platform.md sections 3 and 9).
/// </summary>
/// <remarks>
/// Stateless and thread-safe; nothing allocates. Driver names compare ignoring ASCII case, as SDL matches them.
/// </remarks>
internal static class VideoDriverRules
{
    /// <summary>SDL's invisible-window driver that can create Vulkan surfaces (<see cref="HeadlessMode.Offscreen"/>).</summary>
    public const string Offscreen = "offscreen";

    /// <summary>SDL's driver with no graphics surface at all (<see cref="HeadlessMode.NoSurface"/>).</summary>
    public const string Dummy = "dummy";

    /// <summary>SDL's default driver on macOS, which must run on the process's main thread.</summary>
    public const string Cocoa = "cocoa";

    /// <summary>The headless mode a video driver gives.</summary>
    /// <param name="driver">The driver in use, from <c>SDL_GetCurrentVideoDriver</c>.</param>
    /// <returns>
    /// <see cref="HeadlessMode.Offscreen"/> for <c>offscreen</c>, <see cref="HeadlessMode.NoSurface"/> for
    /// <c>dummy</c>, otherwise <see cref="HeadlessMode.None"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="driver"/> is <see langword="null"/>.</exception>
    public static HeadlessMode HeadlessFrom(string driver)
    {
        ArgumentNullException.ThrowIfNull(driver);
        if (driver.Equals(Offscreen, StringComparison.OrdinalIgnoreCase))
        {
            return HeadlessMode.Offscreen;
        }

        return driver.Equals(Dummy, StringComparison.OrdinalIgnoreCase) ? HeadlessMode.NoSurface : HeadlessMode.None;
    }

    /// <summary>The value of the <c>SDL_VIDEO_DRIVER</c> hint that requests a headless mode.</summary>
    /// <param name="mode">The requested mode.</param>
    /// <returns><c>offscreen</c>, <c>dummy</c>, or <see langword="null"/> for <see cref="HeadlessMode.None"/> (SDL's default).</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="mode"/> isn't a <see cref="HeadlessMode"/> member.</exception>
    public static string? HintFor(HeadlessMode mode) => mode switch
    {
        HeadlessMode.None => null,
        HeadlessMode.Offscreen => Offscreen,
        HeadlessMode.NoSurface => Dummy,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), (int)mode, "Not a HeadlessMode member."),
    };

    /// <summary>
    /// Whether starting SDL video must first check that this is the process's main thread, because the driver SDL
    /// will use is Cocoa, which aborts the process otherwise.
    /// </summary>
    /// <param name="isMacOS">Whether the process runs on macOS; nothing else has the rule.</param>
    /// <param name="environmentDriver">
    /// The <c>SDL_VIDEO_DRIVER</c> environment variable, or <see langword="null"/> when unset. It wins over the
    /// hint. An empty value makes SDL use its default driver, and a list (<c>"dummy,cocoa"</c>) may fall back to
    /// Cocoa, so both count as Cocoa.
    /// </param>
    /// <param name="requestedDriver">The driver the hint will request (<see cref="HintFor"/>), or <see langword="null"/>.</param>
    /// <returns>
    /// <see langword="true"/> on macOS unless the effective driver (the environment variable, else the requested
    /// driver, else <c>cocoa</c>) is exactly <c>dummy</c> or <c>offscreen</c>.
    /// </returns>
    public static bool MustCheckMainThread(bool isMacOS, string? environmentDriver, string? requestedDriver)
    {
        if (!isMacOS)
        {
            return false;
        }

        string effective = environmentDriver ?? requestedDriver ?? Cocoa;
        return !effective.Equals(Dummy, StringComparison.OrdinalIgnoreCase)
            && !effective.Equals(Offscreen, StringComparison.OrdinalIgnoreCase);
    }
}
