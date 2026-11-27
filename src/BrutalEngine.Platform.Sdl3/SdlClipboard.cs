using System;
using System.Runtime.InteropServices;
using Brutal.SdlApi;
using BrutalEngine.Core;

namespace BrutalEngine.Platform.Sdl3;

/// <summary>
/// The OS clipboard's text, as UTF-8 through SDL (docs/design/platform.md section 1, "Input-related window state").
/// </summary>
/// <remarks>
/// Stateless. SDL's clipboard belongs to its video subsystem, so the owning platform calls these on its main thread
/// while its video session is live. Both calls allocate (the string, or the UTF-8 copy of long text, and any error
/// message).
/// </remarks>
internal static class SdlClipboard
{
    /// <summary>Gets the text on the clipboard, through <c>SDL_GetClipboardText</c>.</summary>
    /// <returns>
    /// The text, empty when the clipboard holds none; or <see cref="ErrorKind.Native"/> with SDL's message when SDL
    /// returns no buffer at all.
    /// </returns>
    /// <remarks>
    /// SDL 3 reports a clipboard it can't read as empty text, not as a failure (observed: without SDL video it returns
    /// an empty string), so that reads as an empty string here too. SDL's buffer is copied, then freed with
    /// <c>SDL_free</c>.
    /// </remarks>
    public static unsafe Result<string> GetText()
    {
        byte* text = Sdl.PInvoke.GetClipboardText();
        if (text is null)
        {
            return SdlCheck.Error("SDL_GetClipboardText");
        }

        try
        {
            // Sound: SDL returned a NUL-terminated UTF-8 buffer, which stays valid until the SDL_free below.
            return Marshal.PtrToStringUTF8((nint)text)!;
        }
        finally
        {
            Sdl.PInvoke.Free(text);
        }
    }

    /// <summary>Puts text on the clipboard, through <c>SDL_SetClipboardText</c>.</summary>
    /// <param name="text">The text. An empty string clears the clipboard's text.</param>
    /// <returns>Success, or <see cref="ErrorKind.Native"/> with SDL's message if SDL refused.</returns>
    /// <remarks>
    /// The text is passed as UTF-8, up to its first NUL character, which SDL takes as its end. SDL copies it.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.</exception>
    public static Result SetText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return SdlCheck.Check(Sdl.PInvoke.SetClipboardText(text), "SDL_SetClipboardText");
    }
}
