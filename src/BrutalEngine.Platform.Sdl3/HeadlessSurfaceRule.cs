using System;

namespace BrutalEngine.Platform.Sdl3;

/// <summary>
/// The <c>Offscreen</c> Vulkan rule (docs/design/platform.md section 3): SDL's <c>offscreen</c> driver creates
/// surfaces with <c>VK_EXT_headless_surface</c>, and when no installed driver offers it SDL still creates the window but
/// leaves the extension out of its list. Such a window can't present, so window creation rejects it.
/// </summary>
/// <remarks>Stateless and thread-safe. Allocates nothing.</remarks>
internal static class HeadlessSurfaceRule
{
    /// <summary>The error message for a rejected window, with <see cref="Core.ErrorKind.Unsupported"/>.</summary>
    public const string Message =
        "Headless Vulkan windows need VK_EXT_headless_surface, which no installed Vulkan driver offers. Windows drivers don't; run windowed.";

    /// <summary>
    /// Whether a Vulkan window on <paramref name="videoDriver"/> lacks the headless-surface extension it needs, so its
    /// creation must fail with <see cref="Message"/>.
    /// </summary>
    /// <param name="source">The new window's surface source, read right after SDL created the window.</param>
    /// <param name="videoDriver">The video driver in use, from <c>SDL_GetCurrentVideoDriver</c>.</param>
    /// <returns>
    /// <see langword="true"/> exactly when <paramref name="videoDriver"/> is <c>offscreen</c> (ignoring ASCII case, as
    /// SDL matches driver names) and <c>VK_EXT_headless_surface</c> isn't among the required extensions.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> or <paramref name="videoDriver"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException"><paramref name="source"/>'s window has been disposed.</exception>
    public static bool RequiresHeadlessExtension(IVulkanSurfaceSource source, string videoDriver)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(videoDriver);
        return videoDriver.Equals(VideoDriverRules.Offscreen, StringComparison.OrdinalIgnoreCase)
            && !ContainsExtension(source, "VK_EXT_headless_surface"u8);
    }

    /// <summary>Whether <paramref name="source"/> lists an instance extension, by exact byte comparison.</summary>
    /// <param name="source">The surface source.</param>
    /// <param name="name">The extension name as UTF-8, without a NUL terminator.</param>
    /// <returns><see langword="true"/> when one of the required extensions equals <paramref name="name"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException"><paramref name="source"/>'s window has been disposed.</exception>
    public static bool ContainsExtension(IVulkanSurfaceSource source, ReadOnlySpan<byte> name)
    {
        ArgumentNullException.ThrowIfNull(source);
        int count = source.RequiredInstanceExtensionCount;
        for (int i = 0; i < count; i++)
        {
            if (source.GetRequiredInstanceExtension(i).SequenceEqual(name))
            {
                return true;
            }
        }

        return false;
    }
}
