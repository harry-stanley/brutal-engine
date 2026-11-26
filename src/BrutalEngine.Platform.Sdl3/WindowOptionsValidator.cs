using System;
using System.Globalization;

namespace BrutalEngine.Platform.Sdl3;

/// <summary>
/// Checks <see cref="WindowOptions"/> against the rules it documents, before window creation touches SDL
/// (docs/design/platform.md section 1, "Windows").
/// </summary>
/// <remarks>Stateless and thread-safe. Allocates only when it throws.</remarks>
internal static class WindowOptionsValidator
{
    /// <summary>The largest content size side a window accepts, in content units.</summary>
    public const int MaxContentSide = 16384;

    /// <summary>Throws if an option breaks its rule; returns normally otherwise.</summary>
    /// <param name="options">The window options.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <see cref="WindowOptions.Title"/> is <see langword="null"/>, <see cref="WindowOptions.Mode"/> is
    /// <see cref="WindowMode.ExclusiveFullscreen"/>, or <see cref="WindowOptions.Display"/> is <c>default(DisplayId)</c>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A <see cref="WindowOptions.ContentSize"/> side is outside 1 to 16384, or <see cref="WindowOptions.Mode"/> or
    /// <see cref="WindowOptions.Surface"/> isn't a member of its enum.
    /// </exception>
    public static void Validate(WindowOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Title is null)
        {
            throw new ArgumentException("WindowOptions.Title must not be null.", nameof(options));
        }

        ValidateContentSize(options.ContentSize, nameof(options), "WindowOptions.ContentSize");

        // Range checks, not Enum.IsDefined (reflection). WindowMode's members are 0 to 2, WindowSurface's 0 to 1. The
        // messages use the invariant culture, so a negative value reads "-1" whatever the current minus sign is.
        if (options.Mode is < WindowMode.Windowed or > WindowMode.ExclusiveFullscreen)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options), (int)options.Mode, string.Create(CultureInfo.InvariantCulture, $"WindowOptions.Mode is not a WindowMode member, got {(int)options.Mode}."));
        }

        if (options.Mode == WindowMode.ExclusiveFullscreen)
        {
            throw new ArgumentException(
                "WindowOptions.Mode must not be ExclusiveFullscreen, which needs a display mode: create the window, then call SetExclusiveFullscreen.",
                nameof(options));
        }

        if (options.Surface is < WindowSurface.None or > WindowSurface.Vulkan)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options), (int)options.Surface, string.Create(CultureInfo.InvariantCulture, $"WindowOptions.Surface is not a WindowSurface member, got {(int)options.Surface}."));
        }

        if (options.Display is { Value: 0 })
        {
            throw new ArgumentException(
                "WindowOptions.Display must not be default(DisplayId), which names no display: use null for the primary display.",
                nameof(options));
        }
    }

    /// <summary>Throws if a side of a content size is outside 1 to 16384.</summary>
    /// <param name="contentSize">The size in content units.</param>
    /// <param name="paramName">The parameter the size came in, for the exception.</param>
    /// <param name="property">The name the message uses for the size, such as <c>WindowOptions.ContentSize</c>.</param>
    /// <exception cref="ArgumentOutOfRangeException">A side is outside 1 to 16384.</exception>
    public static void ValidateContentSize(WindowSize contentSize, string paramName, string property)
    {
        ValidateSide(contentSize.Width, paramName, property, "Width");
        ValidateSide(contentSize.Height, paramName, property, "Height");
    }

    private static void ValidateSide(int side, string paramName, string property, string name)
    {
        if (side is < 1 or > MaxContentSide)
        {
            throw new ArgumentOutOfRangeException(paramName, side, string.Create(CultureInfo.InvariantCulture, $"{property}.{name} must be from 1 to {MaxContentSide}, got {side}."));
        }
    }
}
