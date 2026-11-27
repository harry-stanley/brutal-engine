using System;
using System.Runtime.InteropServices;
using Brutal.SdlApi;
using BrutalEngine.Core;

namespace BrutalEngine.Platform.Sdl3;

/// <summary>
/// The platform's OS directories (docs/design/platform.md section 6): the base directory that holds the application,
/// and the per-user data directory for saves, settings and logs.
/// </summary>
/// <remarks>
/// Both are OS paths for <c>System.IO</c> and native code, never virtual file system paths. Not thread-safe: the owning
/// platform calls it on its main thread. Creating one allocates nothing beyond the object itself.
/// </remarks>
internal sealed class SdlPaths
{
    private readonly string _organization;
    private readonly string _application;

    // The override, or the directory SDL resolved; null until then.
    private string? _userDataDirectory;

    /// <summary>Captures the names and the override the user data directory is resolved from.</summary>
    /// <param name="options">
    /// The platform's options, already validated: <see cref="PlatformOptions.Organization"/> and
    /// <see cref="PlatformOptions.Application"/> name the directory, and an absolute
    /// <see cref="PlatformOptions.UserDataDirectory"/> replaces it.
    /// </param>
    /// <remarks>Calls no SDL function and creates no directory. The options aren't kept, only the three values.</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    public SdlPaths(PlatformOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _organization = options.Organization;
        _application = options.Application;
        _userDataDirectory = options.UserDataDirectory;
    }

    /// <summary>Reads the absolute directory that holds the application, from <c>SDL_GetBasePath</c>.</summary>
    /// <returns>
    /// The directory, as SDL reports it (with a trailing directory separator); or <see cref="ErrorKind.Native"/> with
    /// SDL's message if SDL can't tell.
    /// </returns>
    /// <remarks>
    /// Needs no <c>SDL_Init</c>. The platform reads it once, when it is created. BRUTAL's marshaller copies the string,
    /// which SDL owns and keeps, so nothing is freed. Allocates the string.
    /// </remarks>
    public static Result<string> ReadBaseDirectory()
    {
        string? directory = Sdl.PInvoke.GetBasePath();
        return directory is null ? SdlCheck.Error("SDL_GetBasePath") : directory;
    }

    /// <summary>Gets the absolute directory for the user's saves, settings and logs.</summary>
    /// <returns>
    /// The override when one was given, as given, without calling SDL. Otherwise the directory from
    /// <c>SDL_GetPrefPath</c>, which SDL creates if it doesn't exist (with a trailing directory separator); or
    /// <see cref="ErrorKind.Io"/> with SDL's message if it can't be resolved or created.
    /// </returns>
    /// <remarks>
    /// Resolved on the first call, not at construction, so code that never asks never creates a folder in the user's
    /// profile. A resolved directory is kept and later calls return the same string without calling SDL. A failure
    /// isn't kept: the next call asks SDL again. The first call and a failure allocate; later calls don't.
    /// </remarks>
    public unsafe Result<string> GetUserDataDirectory()
    {
        if (_userDataDirectory is not null)
        {
            return _userDataDirectory;
        }

        // Sound: SDL's returned pointer is never read or kept here; it goes straight to TakePrefPath, which copies the
        // string and frees the buffer exactly once (a NULL is only checked, never dereferenced).
        Result<string> resolved = TakePrefPath(Sdl.PInvoke.GetPrefPath(_organization, _application));
        if (resolved.TryGetValue(out string? directory))
        {
            _userDataDirectory = directory;
        }

        return resolved;
    }

    /// <summary>Takes ownership of the buffer <c>SDL_GetPrefPath</c> returned: copies it, then frees it.</summary>
    /// <param name="path">The returned UTF-8 path, or <see langword="null"/> when SDL failed.</param>
    /// <returns>
    /// The path; or, for <see langword="null"/>, <see cref="ErrorKind.Io"/> with the message
    /// <c>"SDL_GetPrefPath failed: &lt;SDL_GetError text&gt;"</c>.
    /// </returns>
    /// <remarks>
    /// The buffer is freed with <c>SDL_free</c> whatever happens, because SDL allocated it for the caller. Allocates the
    /// string or the message.
    /// </remarks>
    internal static unsafe Result<string> TakePrefPath(byte* path)
    {
        if (path is null)
        {
            return new EngineError(ErrorKind.Io, SdlCheck.Error("SDL_GetPrefPath").Message);
        }

        try
        {
            // Sound: SDL returned a NUL-terminated UTF-8 buffer, which stays valid until the SDL_free below.
            return Marshal.PtrToStringUTF8((nint)path)!;
        }
        finally
        {
            Sdl.PInvoke.Free(path);
        }
    }
}
