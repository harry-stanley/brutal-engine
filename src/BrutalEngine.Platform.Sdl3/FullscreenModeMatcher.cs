using System;
using Brutal.SdlApi;

namespace BrutalEngine.Platform.Sdl3;

/// <summary>
/// Finds the SDL fullscreen mode that a <see cref="DisplayMode"/> stands for (docs/design/platform.md section 1,
/// "Exclusive fullscreen"): an exact match on width, height, pixel density and refresh rate.
/// </summary>
/// <remarks>
/// <para>
/// Exact float comparison is sound because a <see cref="DisplayMode"/> the platform returned carries SDL's values
/// unchanged. There is no closest match: a near miss (a refresh rate of 59.94 against 60, say) matches nothing. The
/// display id and pixel format aren't compared: the list is already one display's, and entries that differ only in
/// pixel format resolve to the first one SDL listed.
/// </para>
/// <para>Pure, stateless and thread-safe. Allocates nothing.</para>
/// </remarks>
internal static class FullscreenModeMatcher
{
    /// <summary>Finds the first mode in <paramref name="modes"/> that equals <paramref name="wanted"/> exactly.</summary>
    /// <param name="modes">One display's fullscreen modes, in SDL's order.</param>
    /// <param name="wanted">The mode to find.</param>
    /// <returns>The index of the first exact match, or -1 if none matches.</returns>
    public static int FindExact(ReadOnlySpan<SdlDisplayMode> modes, DisplayMode wanted)
    {
        for (int i = 0; i < modes.Length; i++)
        {
            ref readonly SdlDisplayMode mode = ref modes[i];

            // == rather than float.Equals: NaN never matches, and SDL never reports one.
            if (mode.w == wanted.Width && mode.h == wanted.Height
                && mode.pixel_density == wanted.PixelDensity && mode.refresh_rate == wanted.RefreshRate)
            {
                return i;
            }
        }

        return -1;
    }
}
