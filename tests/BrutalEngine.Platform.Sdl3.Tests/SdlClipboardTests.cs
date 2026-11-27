using System;
using BrutalEngine.Core;
using Xunit;

namespace BrutalEngine.Platform.Sdl3.Tests;

[Collection(SdlNativeTests.Name)]
public sealed class SdlClipboardTests
{
    // SDL's dummy driver has no OS clipboard, so SDL keeps the text itself and it round-trips (observed on Linux with
    // SDL 3 from Ahwoo.Brutal.Sdl 2026.10.3). A refusal is a failed Result: SetText_WithoutVideo_IsNative shows one.
    [Theory]
    [InlineData("plain ASCII")]
    [InlineData("h\u00e9llo \u2713 \U0001D11E \u65e5\u672c")]
    [InlineData("")]
    public void RoundTrip_OrRefusalIsResult(string text)
    {
        using var variable = NativeEnvironmentVariable.Set("SDL_VIDEO_DRIVER", null);
        using SdlVideoSession session = Start();
        Assert.True(SdlClipboard.SetText("something else first").IsSuccess);

        Result set = SdlClipboard.SetText(text);
        Result<string> read = SdlClipboard.GetText();

        Assert.True(set.IsSuccess, set.ToString());
        Assert.True(read.TryGetValue(out string? value), read.ToString());
        Assert.Equal(text, value);
    }

    [Fact]
    public void WithoutVideo_SetIsNativeAndGetIsEmpty()
    {
        // No session: SDL's video subsystem, which owns the clipboard, isn't initialised. SDL 3 reports a clipboard it
        // can't read as empty text, not as a failure (observed).
        Result set = SdlClipboard.SetText("text");
        Result<string> read = SdlClipboard.GetText();

        Assert.True(set.TryGetError(out EngineError error));
        Assert.Equal(ErrorKind.Native, error.Kind);
        Assert.StartsWith("SDL_SetClipboardText failed: ", error.Message, StringComparison.Ordinal);
        Assert.True(read.TryGetValue(out string? value), read.ToString());
        Assert.Equal(string.Empty, value);
    }

    [Fact]
    public void SetText_Null_Throws() => Assert.Throws<ArgumentNullException>(() => SdlClipboard.SetText(null!));

    private static SdlVideoSession Start()
    {
        PlatformOptions options = new() { Organization = "BrutalEngine", Application = "Platform.Sdl3.Tests", Headless = HeadlessMode.NoSurface };
        return SdlVideoSession.Start(options, null).Value;
    }
}
