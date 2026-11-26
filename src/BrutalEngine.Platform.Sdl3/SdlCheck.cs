using System;
using Brutal.SdlApi;
using BrutalEngine.Core;

namespace BrutalEngine.Platform.Sdl3;

/// <summary>
/// The single helper for SDL statuses (docs/design/platform.md section 10): turns a failed SDL call into an
/// <see cref="ErrorKind.Native"/> error that names the call and carries SDL's error text.
/// </summary>
/// <remarks>
/// Stateless. Successful checks allocate nothing; SDL's error text is read, and the message built, only on failure.
/// SDL keeps its error per thread, so a failure is checked on the thread that made the call.
/// </remarks>
internal static class SdlCheck
{
    /// <summary>Checks an SDL bool status, which BRUTAL's raw layer returns as a byte.</summary>
    /// <param name="status">The status: 0 is failure, anything else success.</param>
    /// <param name="call">The SDL function's name, e.g. <c>"SDL_Init"</c>.</param>
    /// <returns>Success, or <see cref="Error"/>'s error for <paramref name="call"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="call"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="call"/> is empty.</exception>
    public static Result Check(byte status, string call)
    {
        ArgumentException.ThrowIfNullOrEmpty(call);
        return status != 0 ? Result.Success : Error(call);
    }

    /// <summary>Checks the pointer an SDL call returned, where <c>NULL</c> is failure.</summary>
    /// <param name="pointer">The returned pointer.</param>
    /// <param name="call">The SDL function's name.</param>
    /// <returns>The pointer, or <see cref="Error"/>'s error for <paramref name="call"/> when it is zero.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="call"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="call"/> is empty.</exception>
    public static Result<nint> NotNull(nint pointer, string call)
    {
        ArgumentException.ThrowIfNullOrEmpty(call);
        return pointer != 0 ? pointer : Error(call);
    }

    /// <summary>Builds the error for a failed call from SDL's current error text on this thread.</summary>
    /// <param name="call">The SDL function's name.</param>
    /// <returns>
    /// An <see cref="ErrorKind.Native"/> error with the message <c>"&lt;call&gt; failed: &lt;SDL_GetError text&gt;"</c>,
    /// or <c>"&lt;call&gt; failed"</c> when SDL has no error text. Allocates the message.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="call"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="call"/> is empty.</exception>
    public static EngineError Error(string call)
    {
        ArgumentException.ThrowIfNullOrEmpty(call);
        string? text = Sdl.PInvoke.GetError();
        string message = string.IsNullOrEmpty(text) ? call + " failed" : call + " failed: " + text;
        return new EngineError(ErrorKind.Native, message);
    }
}
