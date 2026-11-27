using System;
using Brutal.SdlApi;
using BrutalEngine.Core;

namespace BrutalEngine.Platform.Sdl3;

/// <summary>
/// Modal OS message boxes with an OK button, through <c>SDL_ShowSimpleMessageBox</c> (docs/design/platform.md section
/// 8): the platform's boxes, and the stateless fatal-error box for when no platform is running.
/// </summary>
/// <remarks>
/// Stateless. A shown box blocks the calling thread until it is dismissed. Every call allocates (marshalling the text,
/// and any error message), which is fine on these paths.
/// </remarks>
internal static class SdlMessageBoxes
{
    /// <summary>The message for a box asked of a headless platform, with <see cref="ErrorKind.Unavailable"/>.</summary>
    public const string HeadlessMessage =
        "A headless platform shows no message boxes, so automated runs never block on one.";

    /// <summary>The SDL message box call, so tests can record calls with a fake.</summary>
    internal interface INative
    {
        /// <summary>
        /// <c>SDL_ShowSimpleMessageBox</c>, with SDL's signature: shows a box (a null parent handle for none) and blocks
        /// until it is dismissed; a 0 status is failure.
        /// </summary>
        public byte ShowSimpleMessageBox(SdlMessageBoxFlags flags, string title, string message, SdlWindow parent);
    }

    /// <summary>Shows a platform's message box and blocks until it is dismissed.</summary>
    /// <param name="request">The kind, title, message and optional parent window.</param>
    /// <param name="headless">The platform's headless mode: anything but <see cref="HeadlessMode.None"/> shows nothing.</param>
    /// <returns>
    /// Success once dismissed; <see cref="ErrorKind.Unavailable"/> (<see cref="HeadlessMessage"/>) at once, without
    /// calling SDL, when <paramref name="headless"/> isn't <see cref="HeadlessMode.None"/>; or
    /// <see cref="ErrorKind.Native"/> with SDL's message if SDL can't find the parent window or show the box.
    /// </returns>
    /// <remarks>
    /// The arguments are checked first, headless or not, and so is the parent window, by reading its id: that throws
    /// for a disposed parent or off its thread. SDL then finds the parent by that id (<c>SDL_GetWindowFromID</c>); an id
    /// SDL doesn't know, for example once SDL video has stopped, fails with <see cref="ErrorKind.Native"/>. Main
    /// thread only.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="request"/>'s title or message is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="request"/>'s kind isn't a <see cref="MessageBoxKind"/> value.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The parent window has been disposed.</exception>
    /// <exception cref="WrongThreadException">Called off the parent window's thread.</exception>
    public static Result Show(MessageBoxRequest request, HeadlessMode headless) => Show(request, headless, default(SdlNative));

    /// <inheritdoc cref="Show(MessageBoxRequest, HeadlessMode)"/>
    /// <summary>Shows a platform's message box through the given call: a test seam for <see cref="Show(MessageBoxRequest, HeadlessMode)"/>.</summary>
    /// <typeparam name="TNative">The call's type.</typeparam>
    /// <param name="request">The kind, title, message and optional parent window.</param>
    /// <param name="headless">The platform's headless mode.</param>
    /// <param name="native">The call to make.</param>
    internal static Result Show<TNative>(MessageBoxRequest request, HeadlessMode headless, TNative native)
        where TNative : INative
    {
        ArgumentNullException.ThrowIfNull(request);
        SdlMessageBoxFlags flags = request.Kind switch
        {
            MessageBoxKind.Information => SdlMessageBoxFlags.Information,
            MessageBoxKind.Warning => SdlMessageBoxFlags.Warning,
            MessageBoxKind.Error => SdlMessageBoxFlags.Error,
            _ => throw new ArgumentOutOfRangeException(nameof(request), request.Kind, "The request's Kind isn't a MessageBoxKind value."),
        };
        if (request.Title is null || request.Message is null)
        {
            // The record doesn't check its values, so a with-expression can null either one.
            throw new ArgumentException(
                $"The request's {(request.Title is null ? "Title" : "Message")} is null.",
                nameof(request));
        }

        // Reading the id checks the parent's thread and that it isn't disposed: misuse, so before the headless rule.
        uint parentId = request.Parent?.Id.Value ?? 0;
        if (headless != HeadlessMode.None)
        {
            return new EngineError(ErrorKind.Unavailable, HeadlessMessage);
        }

        SdlWindow parent = default;
        if (parentId != 0)
        {
            parent = Sdl.PInvoke.GetWindowFromID(parentId);
            if (parent.IsNull())
            {
                return SdlCheck.Error("SDL_GetWindowFromID");
            }
        }

        return SdlCheck.Check(native.ShowSimpleMessageBox(flags, request.Title, request.Message, parent), "SDL_ShowSimpleMessageBox");
    }

    /// <summary>
    /// Shows a fatal-error box with no platform running, for crash reports: needs no <c>SDL_Init</c> and never throws.
    /// </summary>
    /// <param name="title">The title.</param>
    /// <param name="message">The message.</param>
    /// <returns>
    /// <see langword="true"/> once the box was shown and dismissed; <see langword="false"/> if no box could be shown:
    /// <paramref name="title"/> or <paramref name="message"/> is <see langword="null"/> (SDL isn't called), SDL failed,
    /// or SDL's native library can't be loaded.
    /// </returns>
    /// <remarks>
    /// The box has SDL's error icon and no parent. It catches only the exceptions a missing or broken native library
    /// raises (<see cref="DllNotFoundException"/>, <see cref="EntryPointNotFoundException"/>,
    /// <see cref="BadImageFormatException"/>, and <see cref="TypeInitializationException"/> from BRUTAL's SDL class),
    /// which are the only ones the SDL call raises apart from running out of memory while converting the text. Call
    /// it on the main thread: SDL's documentation asks that of a box without a parent.
    /// </remarks>
    public static bool TryShowFatal(string title, string message) => TryShowFatal(title, message, default(SdlNative));

    /// <inheritdoc cref="TryShowFatal(string, string)"/>
    /// <summary>Shows a fatal-error box through the given call: a test seam for <see cref="TryShowFatal(string, string)"/>.</summary>
    /// <typeparam name="TNative">The call's type.</typeparam>
    /// <param name="title">The title.</param>
    /// <param name="message">The message.</param>
    /// <param name="native">The call to make.</param>
    internal static bool TryShowFatal<TNative>(string title, string message, TNative native)
        where TNative : INative
    {
        if (title is null || message is null)
        {
            return false;
        }

        try
        {
            return native.ShowSimpleMessageBox(SdlMessageBoxFlags.Error, title, message, default) != 0;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException
            or BadImageFormatException or TypeInitializationException)
        {
            // SDL's native library is missing or broken: no box can be shown, which the caller learns from false.
            return false;
        }
    }

    // SDL's own call; a struct, so the default call path allocates no seam object.
    private readonly struct SdlNative : INative
    {
        public byte ShowSimpleMessageBox(SdlMessageBoxFlags flags, string title, string message, SdlWindow parent) =>
            Sdl.PInvoke.ShowSimpleMessageBox(flags, title, message, parent);
    }
}
