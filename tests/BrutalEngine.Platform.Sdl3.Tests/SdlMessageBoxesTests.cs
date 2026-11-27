using System;
using System.Collections.Generic;
using System.Threading;
using Brutal.SdlApi;
using BrutalEngine.Core;
using Xunit;

namespace BrutalEngine.Platform.Sdl3.Tests;

[Collection(SdlNativeTests.Name)]
public sealed class SdlMessageBoxesTests
{
    private static MessageBoxRequest Request { get; } = new(MessageBoxKind.Warning, "Title", "Message");

    [Theory]
    [InlineData(HeadlessMode.Offscreen)]
    [InlineData(HeadlessMode.NoSurface)]
    public void Headless_IsUnavailableWithoutSdl(HeadlessMode headless)
    {
        var native = new FakeBoxes();

        Result shown = SdlMessageBoxes.Show(Request, headless, native);

        Assert.True(shown.TryGetError(out EngineError error));
        Assert.Equal(new EngineError(ErrorKind.Unavailable, SdlMessageBoxes.HeadlessMessage), error);
        Assert.Empty(native.Calls);
    }

    [Theory]
    [InlineData("null request", HeadlessMode.None)]
    [InlineData("null request", HeadlessMode.NoSurface)]
    [InlineData("null title", HeadlessMode.None)]
    [InlineData("null title", HeadlessMode.Offscreen)]
    [InlineData("null message", HeadlessMode.None)]
    [InlineData("null message", HeadlessMode.NoSurface)]
    [InlineData("kind -1", HeadlessMode.None)]
    [InlineData("kind -1", HeadlessMode.NoSurface)]
    [InlineData("kind 3", HeadlessMode.None)]
    [InlineData("kind 3", HeadlessMode.Offscreen)]
    [InlineData("kind min", HeadlessMode.None)]
    public void InvalidArguments_Throw(string row, HeadlessMode headless)
    {
        var native = new FakeBoxes();
        MessageBoxRequest? request = row switch
        {
            "null request" => null,
            "null title" => Request with { Title = null! },
            "null message" => Request with { Message = null! },
            "kind -1" => Request with { Kind = (MessageBoxKind)(-1) },
            "kind 3" => Request with { Kind = (MessageBoxKind)3 },
            _ => Request with { Kind = (MessageBoxKind)int.MinValue },
        };

        Exception thrown = Assert.ThrowsAny<ArgumentException>(() => SdlMessageBoxes.Show(request!, headless, native));

        Type expected = row switch
        {
            "null request" => typeof(ArgumentNullException),
            "null title" or "null message" => typeof(ArgumentException),
            _ => typeof(ArgumentOutOfRangeException),
        };
        Assert.IsType(expected, thrown, exactMatch: true);
        Assert.Equal("request", ((ArgumentException)thrown).ParamName);
        if (row is "null title" or "null message")
        {
            Assert.Contains(row == "null title" ? "Title" : "Message", thrown.Message, StringComparison.Ordinal);
        }

        Assert.Empty(native.Calls);
    }

    // Written from Brutal.Sdl/Generated/Enums/SdlMessageBoxFlags.gen.cs, not from the code under test.
    [Theory]
    [InlineData(MessageBoxKind.Information, 64u)]
    [InlineData(MessageBoxKind.Warning, 32u)]
    [InlineData(MessageBoxKind.Error, 16u)]
    public void Show_NotHeadless_PassesFlagsTextAndNoParent(MessageBoxKind kind, uint flags)
    {
        var native = new FakeBoxes();

        Result shown = SdlMessageBoxes.Show(new MessageBoxRequest(kind, "A title", "A message"), HeadlessMode.None, native);

        Assert.True(shown.IsSuccess, shown.ToString());
        Assert.Equal([new BoxCall((SdlMessageBoxFlags)flags, "A title", "A message", default)], native.Calls);
    }

    [Fact]
    public void EveryKind_HasARow() => Assert.Equal(3, Enum.GetValues<MessageBoxKind>().Length);

    [Fact]
    public void Show_Refused_IsNative()
    {
        var native = new FakeBoxes { Status = 0 };

        Result shown = SdlMessageBoxes.Show(Request, HeadlessMode.None, native);

        Assert.True(shown.TryGetError(out EngineError error));
        Assert.Equal(ErrorKind.Native, error.Kind);
        Assert.StartsWith("SDL_ShowSimpleMessageBox failed", error.Message, StringComparison.Ordinal);
        Assert.Single(native.Calls);
    }

    [Fact]
    public void Show_WithParent_PassesItsSdlWindow()
    {
        using var variable = NativeEnvironmentVariable.Set("SDL_VIDEO_DRIVER", null);
        using SdlVideoSession session = Start();
        using SdlPlatformWindow window = session.CreateWindow(new WindowOptions { Title = "Parent", ContentSize = new(320, 200), Surface = WindowSurface.None }).Value;
        var native = new FakeBoxes();

        Result shown = SdlMessageBoxes.Show(Request with { Parent = window }, HeadlessMode.None, native);

        SdlWindow expected = Sdl.PInvoke.GetWindowFromID(window.Id.Value);
        Assert.False(expected.IsNull());
        Assert.True(shown.IsSuccess, shown.ToString());
        Assert.Equal([new BoxCall(SdlMessageBoxFlags.Warning, "Title", "Message", expected)], native.Calls);
    }

    [Fact]
    public void Show_ParentOffItsThreadOrDisposed_Throws()
    {
        // Misuse of the parent throws even when headless, before SDL is asked anything.
        using var variable = NativeEnvironmentVariable.Set("SDL_VIDEO_DRIVER", null);
        using SdlVideoSession session = Start();
        SdlPlatformWindow window = session.CreateWindow(new WindowOptions { Title = "Parent", ContentSize = new(320, 200), Surface = WindowSurface.None }).Value;
        var native = new FakeBoxes();
        Exception? offThread = null;
        var thread = new Thread(() => offThread = Record.Exception(() => SdlMessageBoxes.Show(Request with { Parent = window }, HeadlessMode.NoSurface, native)));

        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        window.Dispose();

        Assert.IsType<WrongThreadException>(offThread);
        Assert.Throws<ObjectDisposedException>(() => SdlMessageBoxes.Show(Request with { Parent = window }, HeadlessMode.None, native));
        Assert.Throws<ObjectDisposedException>(() => SdlMessageBoxes.Show(Request with { Parent = window }, HeadlessMode.NoSurface, native));
        Assert.Empty(native.Calls);
    }

    [Fact]
    public void Show_ParentUnknownToSdl_IsNative()
    {
        // SDL video stops under a window that wasn't disposed: its id is readable, but SDL no longer knows it. The
        // window isn't disposed afterwards, because SDL has already destroyed it.
        using var variable = NativeEnvironmentVariable.Set("SDL_VIDEO_DRIVER", null);
        SdlVideoSession session = Start();
        SdlPlatformWindow window = session.CreateWindow(new WindowOptions { Title = "Parent", ContentSize = new(320, 200), Surface = WindowSurface.None }).Value;
        session.Dispose();
        var native = new FakeBoxes();

        Result shown = SdlMessageBoxes.Show(Request with { Parent = window }, HeadlessMode.None, native);

        Assert.True(shown.TryGetError(out EngineError error));
        Assert.Equal(ErrorKind.Native, error.Kind);
        Assert.StartsWith("SDL_GetWindowFromID failed", error.Message, StringComparison.Ordinal);
        Assert.Empty(native.Calls);
    }

    [Fact]
    public void Show_OnDummyDriver_IsNative()
    {
        // SDL's dummy driver has no message boxes, and SDL then tries only the driver the hint names (observed), so
        // this shows nothing even on a desktop.
        using var variable = NativeEnvironmentVariable.Set("SDL_VIDEO_DRIVER", null);
        using SdlVideoSession session = Start();

        Result shown = SdlMessageBoxes.Show(Request, HeadlessMode.None);

        Assert.Equal(new EngineError(ErrorKind.Native, "SDL_ShowSimpleMessageBox failed: No message system available"), shown.Error);
    }

    [Fact]
    public void TryShowFatal_SdlFails_ReturnsFalse()
    {
        // No SDL_Init. The variable makes SDL try only the dummy driver's message box, which it hasn't (observed), so
        // the call fails instead of showing a box on a desktop.
        using var variable = NativeEnvironmentVariable.Set("SDL_VIDEO_DRIVER", "dummy");

        Assert.False(SdlMessageBoxes.TryShowFatal("Fatal", "The game stopped."));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(255, true)]
    [InlineData(0, false)]
    public void TryShowFatal_ShowsErrorIconWithoutParent_ReturnsWhetherShown(byte status, bool expected)
    {
        var native = new FakeBoxes { Status = status };

        Assert.Equal(expected, SdlMessageBoxes.TryShowFatal("Fatal", "The game stopped.", native));
        Assert.Equal([new BoxCall(SdlMessageBoxFlags.Error, "Fatal", "The game stopped.", default)], native.Calls);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void TryShowFatal_NullText_ReturnsFalseWithoutSdl(bool nullTitle, bool nullMessage)
    {
        var native = new FakeBoxes();

        Assert.False(SdlMessageBoxes.TryShowFatal(nullTitle ? null! : "Fatal", nullMessage ? null! : "The game stopped.", native));
        Assert.Empty(native.Calls);
    }

    [Theory]
    [InlineData("dll")]
    [InlineData("entry point")]
    [InlineData("bad image")]
    [InlineData("type initializer")]
    public void TryShowFatal_NativeLibraryBroken_ReturnsFalse(string row)
    {
        Exception exception = row switch
        {
            "dll" => new DllNotFoundException("SDL3"),
            "entry point" => new EntryPointNotFoundException("SDL_ShowSimpleMessageBox"),
            "bad image" => new BadImageFormatException("SDL3"),
            _ => new TypeInitializationException("Brutal.SdlApi.Sdl", new DllNotFoundException("SDL3")),
        };
        var native = new FakeBoxes { Throw = exception };

        Assert.False(SdlMessageBoxes.TryShowFatal("Fatal", "The game stopped.", native));
        Assert.Single(native.Calls);
    }

    [Fact]
    public void TryShowFatal_OtherException_Escapes()
    {
        var thrown = new InvalidOperationException("not a native library failure");
        var native = new FakeBoxes { Throw = thrown };

        Assert.Same(thrown, Assert.Throws<InvalidOperationException>(() => SdlMessageBoxes.TryShowFatal("Fatal", "The game stopped.", native)));
    }

    private static SdlVideoSession Start()
    {
        PlatformOptions options = new() { Organization = "BrutalEngine", Application = "Platform.Sdl3.Tests", Headless = HeadlessMode.NoSurface };
        return SdlVideoSession.Start(options, null).Value;
    }

    private readonly record struct BoxCall(SdlMessageBoxFlags Flags, string Title, string Message, SdlWindow Parent);

    /// <summary>Records every call and returns a scripted status, or throws; never shows a box.</summary>
    private sealed class FakeBoxes : SdlMessageBoxes.INative
    {
        public List<BoxCall> Calls { get; } = [];

        public byte Status { get; init; } = 1;

        public Exception? Throw { get; init; }

        public byte ShowSimpleMessageBox(SdlMessageBoxFlags flags, string title, string message, SdlWindow parent)
        {
            Calls.Add(new BoxCall(flags, title, message, parent));
            return Throw is null ? Status : throw Throw;
        }
    }
}
