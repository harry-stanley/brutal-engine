using System;
using System.Collections.Generic;
using System.Globalization;
using Brutal.SdlApi;
using BrutalEngine.Core;

namespace BrutalEngine.Platform.Sdl3;

/// <summary>
/// The displays SDL reports and their fullscreen modes (docs/design/platform.md section 1, "Displays"), as
/// backend-neutral <see cref="DisplayInfo"/> and <see cref="DisplayMode"/> values.
/// </summary>
/// <remarks>
/// <para>
/// Every SDL status is checked. A display SDL doesn't list (never connected, unplugged since, or id 0) is
/// <see cref="ErrorKind.NotFound"/>: an id can outlive its display, so that is an expected failure, not misuse. SDL
/// changes its display list only while events are pumped, so the list stays fixed for the length of one call.
/// </para>
/// <para>
/// <b>Threads:</b> call on the thread that started the video session, as every SDL video call. Stateless.
/// <b>Allocation:</b> both lists allocate; a failure also allocates its message.
/// </para>
/// </remarks>
internal static class SdlDisplays
{
    /// <summary>
    /// Lists every display SDL reports, in SDL's order, with the primary display flagged. Called at creation and when a
    /// display event is pumped.
    /// </summary>
    /// <returns>
    /// One <see cref="DisplayInfo"/> per display, or <see cref="ErrorKind.Native"/> with SDL's message if any query
    /// fails (no partial list).
    /// </returns>
    public static unsafe Result<IReadOnlyList<DisplayInfo>> Build()
    {
        uint primary = Sdl.PInvoke.GetPrimaryDisplay();
        if (primary == 0)
        {
            return SdlCheck.Error("SDL_GetPrimaryDisplay");
        }

        int count;

        // Sound: count is a stack local that outlives the call. On success SDL returns an array of count display ids
        // that it allocated for the caller; it is read before SDL_free releases it.
        uint* ids = Sdl.PInvoke.GetDisplays(&count);
        if (ids is null)
        {
            return SdlCheck.Error("SDL_GetDisplays");
        }

        try
        {
            var displays = new DisplayInfo[count];
            for (int i = 0; i < count; i++)
            {
                Result<DisplayInfo> display = Describe(ids[i], ids[i] == primary);
                if (!display.TryGetValue(out DisplayInfo? info))
                {
                    return display.Error;
                }

                displays[i] = info;
            }

            return displays;
        }
        finally
        {
            Sdl.PInvoke.Free(ids);
        }
    }

    /// <summary>Lists a display's fullscreen modes, in SDL's order (largest first).</summary>
    /// <param name="display">The display.</param>
    /// <returns>
    /// The modes, each with <see cref="DisplayMode.Display"/> set to <paramref name="display"/> (empty when SDL lists
    /// none, as on its <c>dummy</c> and <c>offscreen</c> drivers); <see cref="ErrorKind.NotFound"/> for a display SDL
    /// doesn't list; or <see cref="ErrorKind.Native"/> with SDL's message.
    /// </returns>
    /// <remarks>
    /// Entries that differ only in pixel format, which <see cref="DisplayMode"/> doesn't carry, appear as equal values.
    /// </remarks>
    public static unsafe Result<IReadOnlyList<DisplayMode>> GetFullscreenModes(DisplayId display)
    {
        Result<SdlDisplayMode[]> modes = CopyFullscreenModes(display);
        if (!modes.TryGetValue(out SdlDisplayMode[]? sdlModes))
        {
            return modes.Error;
        }

        var result = new DisplayMode[sdlModes.Length];
        for (int i = 0; i < sdlModes.Length; i++)
        {
            result[i] = ToDisplayMode(sdlModes[i]);
        }

        return result;
    }

    /// <summary>Copies a display's fullscreen modes out of SDL, in SDL's order.</summary>
    /// <param name="display">The display.</param>
    /// <returns>
    /// The copies; <see cref="ErrorKind.NotFound"/> for a display SDL doesn't list; or <see cref="ErrorKind.Native"/>.
    /// </returns>
    /// <remarks>Allocates the array. A copy can be passed to <c>SDL_SetWindowFullscreenMode</c>, which reads the mode's fields.</remarks>
    internal static unsafe Result<SdlDisplayMode[]> CopyFullscreenModes(DisplayId display)
    {
        Result connected = RequireConnected(display.Value);
        if (connected.IsFailure)
        {
            return connected.Error;
        }

        int count;

        // Sound: count is a stack local that outlives the call. SDL returns one allocation holding count mode pointers
        // (and the modes), NULL-terminated; every mode is copied out before SDL_free releases it.
        SdlDisplayMode** modes = Sdl.PInvoke.GetFullscreenDisplayModes(display.Value, &count);
        if (modes is null)
        {
            return SdlCheck.Error("SDL_GetFullscreenDisplayModes");
        }

        try
        {
            var copies = new SdlDisplayMode[count];
            for (int i = 0; i < count; i++)
            {
                copies[i] = *modes[i];
            }

            return copies;
        }
        finally
        {
            Sdl.PInvoke.Free(modes);
        }
    }

    /// <summary>Checks that SDL lists <paramref name="display"/> among its connected displays.</summary>
    /// <param name="display">The display id; 0 is never listed.</param>
    /// <returns>
    /// Success; <see cref="ErrorKind.NotFound"/> naming the display; or <see cref="ErrorKind.Native"/> if
    /// <c>SDL_GetDisplays</c> fails.
    /// </returns>
    internal static unsafe Result RequireConnected(uint display)
    {
        int count;

        // Sound: as in Build.
        uint* ids = Sdl.PInvoke.GetDisplays(&count);
        if (ids is null)
        {
            return SdlCheck.Error("SDL_GetDisplays");
        }

        bool connected = new ReadOnlySpan<uint>(ids, count).Contains(display);
        Sdl.PInvoke.Free(ids);
        return connected
            ? Result.Success
            : new EngineError(
                ErrorKind.NotFound,
                string.Create(CultureInfo.InvariantCulture, $"Display {display} isn't connected: it may have been unplugged since it was listed."));
    }

    /// <summary>Converts SDL's mode to a <see cref="DisplayMode"/>, dropping its pixel format and rational refresh rate.</summary>
    /// <param name="mode">SDL's mode.</param>
    /// <returns>The mode, with <see cref="DisplayMode.Display"/> from SDL's <c>displayID</c>.</returns>
    internal static DisplayMode ToDisplayMode(in SdlDisplayMode mode) => new()
    {
        Display = new DisplayId(mode.displayID),
        Width = mode.w,
        Height = mode.h,
        PixelDensity = mode.pixel_density,
        RefreshRate = mode.refresh_rate,
    };

    private static unsafe Result<DisplayInfo> Describe(uint id, bool isPrimary)
    {
        string? name = Sdl.PInvoke.GetDisplayName(id);
        if (name is null)
        {
            return SdlCheck.Error("SDL_GetDisplayName");
        }

        SdlRect bounds, usable;

        // Sound: both rects are stack locals that outlive the calls, which only write them.
        if (Sdl.PInvoke.GetDisplayBounds(id, &bounds) == 0)
        {
            return SdlCheck.Error("SDL_GetDisplayBounds");
        }

        if (Sdl.PInvoke.GetDisplayUsableBounds(id, &usable) == 0)
        {
            return SdlCheck.Error("SDL_GetDisplayUsableBounds");
        }

        // SDL returns 0.0 on failure.
        float contentScale = Sdl.PInvoke.GetDisplayContentScale(id);
        if (!(contentScale > 0))
        {
            return SdlCheck.Error("SDL_GetDisplayContentScale");
        }

        // Sound: SDL owns the mode and keeps it valid until the display changes, which needs an event pump; it is
        // copied at once.
        SdlDisplayMode* current = Sdl.PInvoke.GetCurrentDisplayMode(id);
        if (current is null)
        {
            return SdlCheck.Error("SDL_GetCurrentDisplayMode");
        }

        return ToDisplayInfo(id, name, bounds, usable, contentScale, *current, isPrimary);
    }

    /// <summary>Builds a <see cref="DisplayInfo"/> from the values SDL reported for one display.</summary>
    /// <param name="id">SDL's display id.</param>
    /// <param name="name">The display's name.</param>
    /// <param name="bounds">The display's bounds, from <c>SDL_GetDisplayBounds</c>.</param>
    /// <param name="usable">The display's usable bounds, from <c>SDL_GetDisplayUsableBounds</c>.</param>
    /// <param name="contentScale">The display's content scale, from <c>SDL_GetDisplayContentScale</c>.</param>
    /// <param name="current">The display's current mode, from <c>SDL_GetCurrentDisplayMode</c>.</param>
    /// <param name="isPrimary">Whether this is SDL's primary display.</param>
    /// <returns>The display, with every field copied from its argument.</returns>
    /// <remarks>Pure; allocates the record.</remarks>
    internal static DisplayInfo ToDisplayInfo(
        uint id,
        string name,
        in SdlRect bounds,
        in SdlRect usable,
        float contentScale,
        in SdlDisplayMode current,
        bool isPrimary) => new()
        {
            Id = new DisplayId(id),
            Name = name,
            Bounds = ToRect(bounds),
            UsableBounds = ToRect(usable),
            ContentScale = contentScale,
            CurrentMode = ToDisplayMode(current),
            IsPrimary = isPrimary,
        };

    private static DisplayRect ToRect(in SdlRect rect) => new(rect.x, rect.y, rect.w, rect.h);
}
