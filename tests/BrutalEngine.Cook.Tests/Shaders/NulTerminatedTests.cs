using BrutalEngine.Cook.Shaders;
using Xunit;

namespace BrutalEngine.Cook.Tests.Shaders;

public sealed class NulTerminatedTests
{
    [Theory]
    [InlineData("", new byte[] { 0x00 })]
    [InlineData("vs", new byte[] { 0x76, 0x73, 0x00 })]
    // U+00E9 is C3 A9 in UTF-8; U+1F600 is F0 9F 98 80 (a surrogate pair in UTF-16).
    [InlineData("\u00e9\U0001F600", new byte[] { 0xC3, 0xA9, 0xF0, 0x9F, 0x98, 0x80, 0x00 })]
    public void Utf8_Text_IsUtf8FollowedByOneNul(string text, byte[] expected)
    {
        Assert.Equal(expected, NulTerminated.Utf8(text));
    }
}
