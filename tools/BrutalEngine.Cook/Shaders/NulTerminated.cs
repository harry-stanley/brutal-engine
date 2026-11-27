using System.Text;

namespace BrutalEngine.Cook.Shaders;

/// <summary>
/// UTF-8 strings for Slang's <c>RefString8</c> parameters. The binding passes a raw pointer and checks nothing
/// (Brutal.Strings/String8/RefString8Marshaller.cs:18-21), so every string needs its own trailing NUL or native code
/// reads past the end.
/// </summary>
internal static class NulTerminated
{
    /// <summary>Encodes <paramref name="text"/> as UTF-8 followed by one NUL byte.</summary>
    /// <param name="text">The text; it must not contain NUL characters (Slang would stop reading at the first).</param>
    /// <returns>A new array: the UTF-8 bytes, then 0.</returns>
    public static byte[] Utf8(string text)
    {
        byte[] bytes = new byte[Encoding.UTF8.GetByteCount(text) + 1];
        Encoding.UTF8.GetBytes(text, bytes);
        return bytes;
    }
}
