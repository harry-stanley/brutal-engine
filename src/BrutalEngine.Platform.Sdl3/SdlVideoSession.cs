using System;
using System.Threading;
using Brutal.SdlApi;
using BrutalEngine.Core;
using BrutalEngine.Core.Logging;

namespace BrutalEngine.Platform.Sdl3;

/// <summary>
/// SDL's video subsystem, started for one platform (docs/design/platform.md sections 3, 9 and 11): validates the
/// options, picks the video driver for the headless mode, starts SDL video and reports the driver really in use.
/// </summary>
/// <remarks>
/// <para>
/// One per process: SDL is process-wide, so a second <see cref="Start"/> while a session is live throws. The count
/// is a primitive static updated with <see cref="Interlocked"/>, so concurrent starts can't both win.
/// </para>
/// <para>
/// Not thread-safe otherwise. The owner starts and disposes it on its main thread (on macOS with <c>cocoa</c>, the
/// process's main thread) and makes every other SDL video call there. Starting allocates (the driver name, the
/// session, error and log text); <see cref="VideoDriver"/> and <see cref="Headless"/> don't.
/// </para>
/// </remarks>
internal sealed class SdlVideoSession : IDisposable
{
    private const string VideoDriverVariable = Sdl.Constants.HintVideoDriver;

    // 1 while a session is live, otherwise 0. A primitive, as the architecture tests require of static state.
    private static int _liveSessions;

    private int _disposed;

    private SdlVideoSession(string videoDriver, HeadlessMode headless)
    {
        VideoDriver = videoDriver;
        Headless = headless;
    }

    /// <summary>The name of the video driver SDL is using, from <c>SDL_GetCurrentVideoDriver</c>.</summary>
    public string VideoDriver { get; }

    /// <summary>The headless mode the driver in use gives, which can differ from the one requested.</summary>
    public HeadlessMode Headless { get; }

    /// <summary>Starts SDL video for <paramref name="options"/>.</summary>
    /// <param name="options">The platform options; validated first.</param>
    /// <param name="sdlOptions">The SDL-specific options, or <see langword="null"/> for the defaults.</param>
    /// <returns>
    /// The session, or a failure: <see cref="ErrorKind.Unsupported"/> when the requested headless mode isn't the one
    /// the driver in use gives, or (on macOS) when the <c>cocoa</c> driver would start off the process's main thread;
    /// <see cref="ErrorKind.Native"/> with SDL's message when an SDL call fails. A failed start leaves SDL stopped and
    /// no session live.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An option breaks its rule (see <see cref="OptionsValidator"/>).</exception>
    /// <exception cref="ArgumentOutOfRangeException">A capacity or the headless mode is out of range.</exception>
    /// <exception cref="InvalidOperationException">Another session is live.</exception>
    public static Result<SdlVideoSession> Start(PlatformOptions options, SdlPlatformOptions? sdlOptions)
    {
        OptionsValidator.Validate(options, sdlOptions);
        if (Interlocked.CompareExchange(ref _liveSessions, 1, 0) != 0)
        {
            throw new InvalidOperationException(
                "SDL video is already started: one platform per process. Dispose the live platform before creating another.");
        }

        Result<SdlVideoSession> result;
        try
        {
            result = StartVideo(options, sdlOptions);
        }
        catch
        {
            _ = Interlocked.Exchange(ref _liveSessions, 0);
            throw;
        }

        if (result.IsFailure)
        {
            _ = Interlocked.Exchange(ref _liveSessions, 0);
        }

        return result;
    }

    /// <summary>Stops SDL (<c>SDL_Quit</c>) and ends the session, once; later calls do nothing.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Sdl.PInvoke.Quit();
        _ = Interlocked.Exchange(ref _liveSessions, 0);
    }

    private static Result<SdlVideoSession> StartVideo(PlatformOptions options, SdlPlatformOptions? sdlOptions)
    {
        string? requestedDriver = VideoDriverRules.HintFor(options.Headless);

        // Cocoa aborts the process when it starts off the main thread, so the check comes before SDL_Init.
        if (OperatingSystem.IsMacOS())
        {
            Result<string?> environmentDriver = ReadEnvironmentVariable(VideoDriverVariable);
            if (!environmentDriver.TryGetValue(out string? driver))
            {
                return environmentDriver.Error;
            }

            if (VideoDriverRules.MustCheckMainThread(isMacOS: true, driver, requestedDriver) && Sdl.PInvoke.IsMainThread() == 0)
            {
                return new EngineError(
                    ErrorKind.Unsupported,
                    "SDL's cocoa video driver must start on the process's main thread. Create the platform there, or use a headless mode.");
            }
        }

        LogCategory log = options.Logger?.CreateCategory("Platform") ?? default;
        if (requestedDriver is not null)
        {
            SetHint(VideoDriverVariable, requestedDriver, log);
        }

        if (sdlOptions?.VulkanLibraryPath is { } vulkanLibrary)
        {
            SetHint(Sdl.Constants.HintVulkanLibrary, vulkanLibrary, log);
        }

        Result init = SdlCheck.Check(Sdl.PInvoke.Init(SdlInitFlags.Video), "SDL_Init");
        if (init.IsFailure)
        {
            // SDL_Quit after a failed SDL_Init clears the hints set above; SDL documents it as safe.
            Sdl.PInvoke.Quit();
            return init.Error;
        }

        string? videoDriver = Sdl.PInvoke.GetCurrentVideoDriver();
        if (videoDriver is null)
        {
            EngineError error = SdlCheck.Error("SDL_GetCurrentVideoDriver");
            Sdl.PInvoke.Quit();
            return error;
        }

        HeadlessMode headless = VideoDriverRules.HeadlessFrom(videoDriver);
        if (options.Headless != HeadlessMode.None && headless != options.Headless)
        {
            Sdl.PInvoke.Quit();
            return new EngineError(
                ErrorKind.Unsupported,
                $"The requested headless mode needs SDL's '{requestedDriver}' video driver, but SDL is using '{videoDriver}': the {VideoDriverVariable} environment variable overrides the request. Unset it, or request HeadlessMode.None.");
        }

        return new SdlVideoSession(videoDriver, headless);
    }

    // A refused hint is not an error: the environment variable of the same name overrides it (section 3).
    private static void SetHint(string name, string value, LogCategory log)
    {
        if (Sdl.PInvoke.SetHint(name, value) == 0)
        {
            log.Information($"SDL refused the hint {name}={value}: the {name} environment variable overrides it.");
        }
    }

    private static Result<string?> ReadEnvironmentVariable(string name)
    {
        Result<nint> environment = SdlCheck.NotNull(Sdl.PInvoke.GetEnvironment(), "SDL_GetEnvironment");
        return environment.TryGetValue(out nint handle)
            ? Sdl.PInvoke.GetEnvironmentVariable(handle, name)
            : environment.Error;
    }
}
