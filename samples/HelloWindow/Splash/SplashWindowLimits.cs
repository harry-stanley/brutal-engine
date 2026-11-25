using System;

namespace HelloWindow.Splash;

/// <summary>Keeps a window big enough for the splash while it lasts.</summary>
/// <remarks>A blit scales the logo to any window, but a 1:1 copy needs the logo to fit inside the margins. If the window
/// shrank below that during the splash, the next swapchain couldn't show the logo and the run would fail. So, on the
/// copy path only, the window gets a minimum size for as long as the splash lasts, recomputed for every new swapchain
/// in that time, because the pixel density can change under it.</remarks>
internal static class SplashWindowLimits
{
    /// <summary>Computes the window's minimum size during the splash.</summary>
    /// <param name="transfer">How the logo reaches the swapchain image (blit or copy).</param>
    /// <param name="logo">The logo's size in pixels (at least 1x1).</param>
    /// <param name="margin">The margin given to <see cref="SplashLayout.Fit"/> (0 or more).</param>
    /// <param name="pixelDensity">Pixels per window coordinate (<c>SDL_GetWindowPixelDensity</c>), positive.</param>
    /// <returns>The minimum client size in window coordinates (what <c>SDL_SetWindowMinimumSize</c> takes): the
    /// smallest that still gives the logo plus a margin on each side in pixels. 0x0 (no limit) for a blit.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="transfer"/> is neither a blit nor a copy, or an
    /// argument is out of range.</exception>
    public static PixelSize MinimumWindowSize(SplashTransfer transfer, PixelSize logo, int margin, float pixelDensity)
    {
        if (transfer is not (SplashTransfer.Blit or SplashTransfer.Copy))
        {
            throw new ArgumentOutOfRangeException(nameof(transfer), transfer, "Only a blit or a copy draws the splash.");
        }

        if (logo.Width < 1 || logo.Height < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(logo), logo, "The logo must be at least 1x1 pixels.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(margin);
        if (!float.IsFinite(pixelDensity) || pixelDensity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pixelDensity), pixelDensity, "The pixel density must be positive.");
        }

        if (transfer == SplashTransfer.Blit)
        {
            return default;
        }

        // SplashLayout.Fit keeps the logo's size exactly when it fits inside the margins. Rounding the window
        // coordinates up keeps width x density at or above that many pixels.
        return new PixelSize(ToWindowCoordinates(logo.Width + (2L * margin), pixelDensity), ToWindowCoordinates(logo.Height + (2L * margin), pixelDensity));
    }

    /// <summary>Recomputes the window's minimum size for a new swapchain during the splash, from the current pixel
    /// density.</summary>
    /// <param name="applied">The minimum the window has now (0x0 for none). Set to the new minimum if it changed; left
    /// alone if an argument is invalid.</param>
    /// <param name="transfer">How the logo reaches the swapchain image (blit or copy).</param>
    /// <param name="logo">The logo's size in pixels (at least 1x1).</param>
    /// <param name="margin">The margin given to <see cref="SplashLayout.Fit"/> (0 or more).</param>
    /// <param name="pixelDensity">Pixels per window coordinate now, positive. It changes when the window moves to a
    /// display with another density, which is why the minimum is recomputed rather than kept.</param>
    /// <returns><see langword="true"/> if <paramref name="applied"/> changed, so <c>SDL_SetWindowMinimumSize</c> must
    /// be called with it.</returns>
    /// <exception cref="ArgumentOutOfRangeException">As for <see cref="MinimumWindowSize"/>.</exception>
    public static bool TryUpdate(ref PixelSize applied, SplashTransfer transfer, PixelSize logo, int margin, float pixelDensity)
    {
        PixelSize required = MinimumWindowSize(transfer, logo, margin, pixelDensity);
        if (required == applied)
        {
            return false;
        }

        applied = required;
        return true;
    }

    private static int ToWindowCoordinates(long pixels, float pixelDensity) => (int)Math.Ceiling(pixels / (double)pixelDensity);
}
