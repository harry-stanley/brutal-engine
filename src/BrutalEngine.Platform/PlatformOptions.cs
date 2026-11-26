using System;
using BrutalEngine.Core.Logging;

namespace BrutalEngine.Platform;

/// <summary>
/// Backend-neutral settings for creating a platform, passed to the backend's factory (<c>SdlPlatform.Create</c> in
/// <c>BrutalEngine.Platform.Sdl3</c>).
/// </summary>
/// <remarks>
/// <para>
/// The record only describes the platform; it doesn't check its values. The backend validates them when it creates the
/// platform. A <see langword="null"/> options object throws <see cref="ArgumentNullException"/>. A
/// <see langword="null"/> <see cref="Organization"/>, <see cref="Application"/> or <see cref="Clock"/>, a name that
/// doesn't match its pattern, or a <see cref="UserDataDirectory"/> that isn't absolute throws
/// <see cref="ArgumentException"/>. An <see cref="EventCapacity"/> or <see cref="TextCapacityBytes"/> outside its
/// range, or an undefined <see cref="Headless"/> value, throws <see cref="ArgumentOutOfRangeException"/>.
/// </para>
/// <para>
/// Equality compares every property; <see cref="Clock"/> and <see cref="Logger"/> compare by reference. An immutable
/// record: the platform copies what it needs when it is created, so a later <c>with</c> copy has no effect on it. Safe
/// to share between threads. Creating one allocates; reading it doesn't.
/// </para>
/// </remarks>
public sealed record PlatformOptions
{
    /// <summary>
    /// The organisation (developer or publisher) that makes the application. Required: 1 to 64 characters, each an
    /// ASCII letter or digit, a space, <c>.</c>, <c>_</c> or <c>-</c>.
    /// </summary>
    /// <remarks>
    /// With <see cref="Application"/>, it names the per-user data directory, so it is restricted to characters that are
    /// valid in a folder name on every OS.
    /// </remarks>
    public required string Organization { get; init; }

    /// <summary>
    /// The application's name. Required, with the same rule as <see cref="Organization"/>: 1 to 64 characters, each an
    /// ASCII letter or digit, a space, <c>.</c>, <c>_</c> or <c>-</c>.
    /// </summary>
    public required string Application { get; init; }

    /// <summary>
    /// The headless mode to run in. Default <see cref="HeadlessMode.None"/>: real windows on the desktop.
    /// </summary>
    /// <remarks>
    /// The mode the platform really runs in can differ when the user forces a video driver; see
    /// <see cref="HeadlessMode"/>.
    /// </remarks>
    public HeadlessMode Headless { get; init; }

    /// <summary>
    /// How many events one pump of the platform's event queue can hold. Default 1024 (40 KiB); valid from 64 to 65536.
    /// </summary>
    /// <remarks>
    /// Fixed when the platform is created. Events beyond it stay in the OS queue, in order, and arrive with the next
    /// pump: they are deferred, never dropped.
    /// </remarks>
    public int EventCapacity { get; init; } = 1024;

    /// <summary>
    /// How many bytes of UTF-8 text input one pump can hold. Default 8192; valid from 2048 to 1048576.
    /// </summary>
    /// <remarks>
    /// Fixed when the platform is created. The minimum is twice the 1024-byte reserve the pump keeps free for a text
    /// event, so the reserve never takes the whole buffer.
    /// </remarks>
    public int TextCapacityBytes { get; init; } = 8192;

    /// <summary>
    /// The clock for frame timing and event timestamps. Default <see cref="TimeProvider.System"/>. Must not be
    /// <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// The platform exposes it unchanged, so the loop, input and logs share one time base. Tests pass a
    /// <see cref="TimeProvider"/> subclass that returns scripted timestamps.
    /// </remarks>
    public TimeProvider Clock { get; init; } = TimeProvider.System;

    /// <summary>
    /// An absolute directory for per-user data (saves, settings, logs), or <see langword="null"/> (the default) for the
    /// OS's per-user directory named after <see cref="Organization"/> and <see cref="Application"/>.
    /// </summary>
    /// <remarks>
    /// The backend resolves the OS directory on first use, not at creation, because resolving it creates it. A
    /// directory given here replaces it, so tests can pass a temporary directory instead of writing to the user's
    /// profile.
    /// </remarks>
    public string? UserDataDirectory { get; init; }

    /// <summary>
    /// The engine logger the platform writes to, under the category <c>"Platform"</c>; the backend also forwards the
    /// BRUTAL framework's own logs into it. Default <see langword="null"/>: nothing is logged.
    /// </summary>
    /// <remarks>
    /// Not owned: the platform never disposes it, and the caller keeps it alive until the platform is disposed.
    /// </remarks>
    public Logger? Logger { get; init; }
}
