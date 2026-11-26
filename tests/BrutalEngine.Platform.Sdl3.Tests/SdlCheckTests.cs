using System;
using Brutal.SdlApi;
using BrutalEngine.Core;
using Xunit;

namespace BrutalEngine.Platform.Sdl3.Tests;

[Collection(SdlNativeTests.Name)]
public sealed class SdlCheckTests
{
    [Fact]
    public void ZeroStatus_IsNativeErrorWithSdlText()
    {
        // A NULL environment is an invalid parameter: SDL returns false and sets its error text, with no SDL_Init.
        byte status = Sdl.PInvoke.SetEnvironmentVariable(0, "BRUTALENGINE_TEST", "1", 1);
        string? text = Sdl.PInvoke.GetError();

        Result result = SdlCheck.Check(status, "SDL_SetEnvironmentVariable");

        Assert.Equal(0, status);
        Assert.False(string.IsNullOrEmpty(text));
        Assert.True(result.TryGetError(out EngineError error));
        Assert.Equal(ErrorKind.Native, error.Kind);
        Assert.Equal("SDL_SetEnvironmentVariable failed: " + text, error.Message);
    }

    [Fact]
    public void NullPointer_IsNativeErrorWithSdlText()
    {
        _ = Sdl.PInvoke.SetEnvironmentVariable(0, "BRUTALENGINE_TEST", "1", 1);
        string? text = Sdl.PInvoke.GetError();

        Result<nint> result = SdlCheck.NotNull(0, "SDL_Example");

        Assert.False(string.IsNullOrEmpty(text));
        Assert.True(result.TryGetError(out EngineError error));
        Assert.Equal(ErrorKind.Native, error.Kind);
        Assert.Equal("SDL_Example failed: " + text, error.Message);
    }

    [Fact]
    public void Error_NoSdlText_NamesTheCall()
    {
        Assert.NotEqual(0, Sdl.PInvoke.ClearError());

        EngineError error = SdlCheck.Error("SDL_Example");

        Assert.Equal(new EngineError(ErrorKind.Native, "SDL_Example failed"), error);
    }

    [Fact]
    public void NonZeroStatus_IsSuccess()
    {
        Assert.True(SdlCheck.Check(1, "SDL_Example").IsSuccess);
        Assert.True(SdlCheck.Check(255, "SDL_Example").IsSuccess);
    }

    [Fact]
    public void NonNullPointer_ReturnsIt()
    {
        Assert.Equal(-1, SdlCheck.NotNull(-1, "SDL_Example").Value);
        Assert.Equal(0x1000, SdlCheck.NotNull(0x1000, "SDL_Example").Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NullOrEmptyCall_Throws(string? call)
    {
        _ = Assert.ThrowsAny<ArgumentException>(() => SdlCheck.Check(1, call!));
        _ = Assert.ThrowsAny<ArgumentException>(() => SdlCheck.NotNull(1, call!));
        _ = Assert.ThrowsAny<ArgumentException>(() => SdlCheck.Error(call!));
    }
}
