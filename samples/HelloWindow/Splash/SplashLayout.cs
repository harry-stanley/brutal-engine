using System;

namespace HelloWindow.Splash;

/// <summary>Places the splash logo in the swapchain image: centred, scaled down to fit, never scaled up.</summary>
/// <remarks>BRUTAL LICENSE 2.2 allows the logo to be scaled but not otherwise altered, so the aspect ratio is kept (to
/// within the rounding to whole pixels).</remarks>
internal static class SplashLayout
{
    /// <summary>Computes where the logo goes.</summary>
    /// <param name="logo">The logo's size in pixels (at least 1x1).</param>
    /// <param name="target">The swapchain image's size in pixels (at least 1x1).</param>
    /// <param name="margin">The minimum space in pixels between the logo and each edge of the target (0 or more).
    /// It shrinks where the target is too small to keep it and still leave at least one pixel for the logo.</param>
    /// <returns>The destination rectangle: inside <paramref name="target"/>, centred, at most the logo's size, at least
    /// 1x1, with the logo's aspect ratio. A logo that fits inside the margins keeps its size.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A size is less than 1x1, or <paramref name="margin"/> is
    /// negative.</exception>
    public static PixelRect Fit(PixelSize logo, PixelSize target, int margin)
    {
        if (logo.Width < 1 || logo.Height < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(logo), logo, "The logo must be at least 1x1 pixels.");
        }

        if (target.Width < 1 || target.Height < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(target), target, "The target must be at least 1x1 pixels.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(margin);

        // The space inside the margins, keeping at least one pixel on each axis.
        long availableWidth = target.Width - (2L * Math.Min(margin, (target.Width - 1) / 2));
        long availableHeight = target.Height - (2L * Math.Min(margin, (target.Height - 1) / 2));

        long width = logo.Width;
        long height = logo.Height;
        if (width > availableWidth || height > availableHeight)
        {
            // Scale down by the tighter axis, which fills its space exactly, and round the other one down so it stays
            // inside. Compare availableWidth / width with availableHeight / height without dividing.
            if (availableWidth * logo.Height <= availableHeight * logo.Width)
            {
                width = availableWidth;
                height = Math.Max(1, availableWidth * logo.Height / logo.Width);
            }
            else
            {
                height = availableHeight;
                width = Math.Max(1, availableHeight * logo.Width / logo.Height);
            }
        }

        // Both are at most the target's int size here, so the casts can't overflow.
        return new PixelRect((int)((target.Width - width) / 2), (int)((target.Height - height) / 2), (int)width, (int)height);
    }
}
