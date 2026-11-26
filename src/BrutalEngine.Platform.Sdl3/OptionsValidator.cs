using System;
using System.IO;

namespace BrutalEngine.Platform.Sdl3;

/// <summary>
/// Checks <see cref="PlatformOptions"/> and <see cref="SdlPlatformOptions"/> against the rules they document, before
/// anything touches SDL (docs/design/platform.md section 11).
/// </summary>
/// <remarks>Stateless and thread-safe. Allocates only when it throws.</remarks>
internal static class OptionsValidator
{
    private const int MaxNameLength = 64;

    /// <summary>Throws if an option breaks its rule; returns normally otherwise.</summary>
    /// <param name="options">The platform options.</param>
    /// <param name="sdlOptions">The SDL options, or <see langword="null"/> for the defaults.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <see cref="PlatformOptions.Organization"/> or <see cref="PlatformOptions.Application"/> is
    /// <see langword="null"/> or doesn't match <c>[A-Za-z0-9 ._-]{1,64}</c>; <see cref="PlatformOptions.Clock"/> is
    /// <see langword="null"/>; or <see cref="PlatformOptions.UserDataDirectory"/> or
    /// <see cref="SdlPlatformOptions.VulkanLibraryPath"/> is set and isn't fully qualified.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="PlatformOptions.Headless"/> isn't a member, <see cref="PlatformOptions.EventCapacity"/> is outside
    /// 64 to 65536, or <see cref="PlatformOptions.TextCapacityBytes"/> is outside 2048 to 1048576.
    /// </exception>
    public static void Validate(PlatformOptions options, SdlPlatformOptions? sdlOptions)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateName(options.Organization, "PlatformOptions.Organization", nameof(options));
        ValidateName(options.Application, "PlatformOptions.Application", nameof(options));

        // A range check, not Enum.IsDefined (reflection); HeadlessMode's members are 0 to 2.
        if (options.Headless is < HeadlessMode.None or > HeadlessMode.NoSurface)
        {
            throw new ArgumentOutOfRangeException(nameof(options), (int)options.Headless, "PlatformOptions.Headless is not a HeadlessMode member.");
        }

        ValidateRange(options.EventCapacity, 64, 65536, "PlatformOptions.EventCapacity", nameof(options));
        ValidateRange(options.TextCapacityBytes, 2048, 1048576, "PlatformOptions.TextCapacityBytes", nameof(options));
        if (options.Clock is null)
        {
            throw new ArgumentException("PlatformOptions.Clock must not be null.", nameof(options));
        }

        ValidateFullPath(options.UserDataDirectory, "PlatformOptions.UserDataDirectory", nameof(options));
        ValidateFullPath(sdlOptions?.VulkanLibraryPath, "SdlPlatformOptions.VulkanLibraryPath", nameof(sdlOptions));
    }

    private static void ValidateName(string? name, string property, string parameter)
    {
        if (name is null || name.Length is 0 or > MaxNameLength || !HasOnlyNameCharacters(name))
        {
            throw new ArgumentException(
                $"{property} must be 1 to 64 characters, each an ASCII letter or digit, a space, '.', '_' or '-'.", parameter);
        }
    }

    private static bool HasOnlyNameCharacters(string name)
    {
        foreach (char c in name)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not (' ' or '.' or '_' or '-'))
            {
                return false;
            }
        }

        return true;
    }

    private static void ValidateRange(int value, int minimum, int maximum, string property, string parameter)
    {
        if (value < minimum || value > maximum)
        {
            throw new ArgumentOutOfRangeException(parameter, value, $"{property} must be from {minimum} to {maximum}.");
        }
    }

    private static void ValidateFullPath(string? path, string property, string parameter)
    {
        if (path is not null && !Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException($"{property} must be a fully qualified path when set.", parameter);
        }
    }
}
