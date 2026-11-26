using System;
using System.Threading;
using Brutal.SdlApi;
using BrutalEngine.Core;
using Xunit;

namespace BrutalEngine.Platform.Sdl3.Tests;

// The window modes and the IWindow declaration. SDL's dummy and offscreen drivers accept borderless fullscreen, apply
// it at once and list no fullscreen modes, so every exclusive request there is NotFound (observed on Windows with SDL
// 3.4.12). Neither driver refuses a fullscreen call, so the Native path is covered by review, not by a test here.
[Collection(SdlNativeTests.Name)]
public sealed unsafe class SdlPlatformWindowModeTests
{
    private const int WorkerTimeoutSeconds = 30;

    private static WindowOptions NoSurface { get; } = new() { Title = "Platform.Sdl3.Tests", ContentSize = new(320, 200), Surface = WindowSurface.None };

    [Fact]
    public void SetExclusiveFullscreen_Default_Throws()
    {
        using var variable = NativeEnvironmentVariable.Set("SDL_VIDEO_DRIVER", null);
        using SdlVideoSession session = Start(HeadlessMode.NoSurface);
        using SdlPlatformWindow window = session.CreateWindow(NoSurface).Value;

        ArgumentException thrown = Assert.Throws<ArgumentException>(() => window.SetExclusiveFullscreen(default));

        Assert.Equal("mode", thrown.ParamName);
        Assert.False(IsNativeFullscreen(window));
    }

    [Theory]
    [InlineData("hand-built")]
    [InlineData("current mode")]
    [InlineData("unknown display")]
    public void SetExclusiveFullscreen_HandBuiltMode_IsNotFound(string row)
    {
        using var variable = NativeEnvironmentVariable.Set("SDL_VIDEO_DRIVER", null);
        using SdlVideoSession session = Start(HeadlessMode.NoSurface);
        using SdlPlatformWindow window = session.CreateWindow(NoSurface).Value;
        DisplayInfo display = Assert.Single(SdlDisplays.Build().Value);

        // The display's current mode isn't in its fullscreen list here (dummy lists none), so it can fail too.
        DisplayMode mode = row switch
        {
            "hand-built" => new DisplayMode { Display = display.Id, Width = 12345, Height = 678, PixelDensity = 1f, RefreshRate = 60f },
            "current mode" => display.CurrentMode,
            _ => display.CurrentMode with { Display = new DisplayId(display.Id.Value + 1) },
        };

        Result result = window.SetExclusiveFullscreen(mode);

        Assert.True(result.TryGetError(out EngineError error));
        Assert.Equal(ErrorKind.NotFound, error.Kind);
        Assert.Contains(row == "unknown display" ? "isn't connected" : "matches no fullscreen mode", error.Message, StringComparison.Ordinal);
        Assert.False(IsNativeFullscreen(window));
    }

    [Fact]
    public void SetMode_ExclusiveFullscreen_Throws()
    {
        using var variable = NativeEnvironmentVariable.Set("SDL_VIDEO_DRIVER", null);
        using SdlVideoSession session = Start(HeadlessMode.NoSurface);
        using SdlPlatformWindow window = session.CreateWindow(NoSurface).Value;

        ArgumentException thrown = Assert.Throws<ArgumentException>(() => window.SetMode(WindowMode.ExclusiveFullscreen));

        Assert.Equal("mode", thrown.ParamName);
        Assert.Contains("SetExclusiveFullscreen", thrown.Message, StringComparison.Ordinal);
        Assert.False(IsNativeFullscreen(window));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    public void SetMode_Undefined_Throws(int value)
    {
        using var variable = NativeEnvironmentVariable.Set("SDL_VIDEO_DRIVER", null);
        using SdlVideoSession session = Start(HeadlessMode.NoSurface);
        using SdlPlatformWindow window = session.CreateWindow(NoSurface).Value;

        ArgumentOutOfRangeException thrown = Assert.Throws<ArgumentOutOfRangeException>(() => window.SetMode((WindowMode)value));

        Assert.Equal("mode", thrown.ParamName);
        Assert.False(IsNativeFullscreen(window));
    }

    [Theory]
    [InlineData(HeadlessMode.NoSurface)]
    [InlineData(HeadlessMode.Offscreen)]
    public void SetMode_BorderlessThenWindowed_SwitchesWindow(HeadlessMode headless)
    {
        using var variable = NativeEnvironmentVariable.Set("SDL_VIDEO_DRIVER", null);
        using SdlVideoSession session = Start(headless);
        using SdlPlatformWindow window = session.CreateWindow(NoSurface).Value;
        SdlWindow handle = Sdl.PInvoke.GetWindowFromID(window.Id.Value);

        AssertSuccess(window.SetMode(WindowMode.BorderlessFullscreen));

        // Borderless: fullscreen without a fullscreen display mode. The snapshot changes on the next refresh.
        Assert.True(IsNativeFullscreen(window));
        Assert.True(Sdl.PInvoke.GetWindowFullscreenMode(handle) is null);
        Assert.Equal(WindowMode.Windowed, window.Mode);
        AssertSuccess(window.RefreshSnapshot());
        Assert.Equal(WindowMode.BorderlessFullscreen, window.Mode);
        Assert.Equal(new WindowSize(1024, 768), window.Size);

        AssertSuccess(window.SetMode(WindowMode.Windowed));

        Assert.False(IsNativeFullscreen(window));
        AssertSuccess(window.RefreshSnapshot());
        Assert.Equal(WindowMode.Windowed, window.Mode);
        Assert.Equal(NoSurface.ContentSize, window.ContentSize);
    }

    // Each row ends windowed at the content size it asked for last. A content scale of 2 makes the creation estimate
    // 640 by 400 where the window's own scale is 1, so a window created fullscreen keeps a wrong windowed size unless
    // leaving fullscreen applies the content-size model. Dummy also forgets a size set while fullscreen.
    [Theory]
    [InlineData("created borderless", HeadlessMode.NoSurface, 320, 200)]
    [InlineData("created borderless", HeadlessMode.Offscreen, 320, 200)]
    [InlineData("created borderless, resized", HeadlessMode.NoSurface, 400, 300)]
    [InlineData("created windowed, borderless, resized", HeadlessMode.NoSurface, 400, 300)]
    public void SetMode_Windowed_AppliesRequestedContentSize(string row, HeadlessMode headless, int width, int height)
    {
        using var variable = NativeEnvironmentVariable.Set("SDL_VIDEO_DRIVER", null);
        using SdlVideoSession session = Start(headless);
        WindowMode created = row.StartsWith("created borderless", StringComparison.Ordinal) ? WindowMode.BorderlessFullscreen : WindowMode.Windowed;
        using SdlPlatformWindow window = SdlPlatformWindow.Create(NoSurface with { Mode = created }, session.VideoDriver, session.Headless, estimateScale: 2f).Value;
        if (created == WindowMode.Windowed)
        {
            AssertSuccess(window.SetMode(WindowMode.BorderlessFullscreen));
        }

        if (row.EndsWith("resized", StringComparison.Ordinal))
        {
            AssertSuccess(window.SetContentSize(new WindowSize(400, 300)));
        }

        AssertSuccess(window.SetMode(WindowMode.Windowed));

        Assert.False(IsNativeFullscreen(window));
        Assert.Equal(new WindowSize(width, height), NativeSize(window));
        AssertSuccess(window.RefreshSnapshot());
        Assert.Equal(new WindowSize(width, height), window.ContentSize);
    }

    // The pending content size is applied once. After that the window's windowed size is SDL's own: here the OS
    // resizes the window (a raw SDL_SetWindowSize, as a user dragging its edge would), and a second round trip through
    // fullscreen must come back at that size, not at the size the window was created with.
    [Theory]
    [InlineData(HeadlessMode.NoSurface)]
    [InlineData(HeadlessMode.Offscreen)]
    public void SetMode_WindowedAgainAfterOsResize_KeepsSdlSize(HeadlessMode headless)
    {
        using var variable = NativeEnvironmentVariable.Set("SDL_VIDEO_DRIVER", null);
        using SdlVideoSession session = Start(headless);
        using SdlPlatformWindow window = SdlPlatformWindow.Create(NoSurface with { Mode = WindowMode.BorderlessFullscreen }, session.VideoDriver, session.Headless, estimateScale: 2f).Value;
        SdlWindow handle = Sdl.PInvoke.GetWindowFromID(window.Id.Value);
        AssertSuccess(window.SetMode(WindowMode.Windowed));
        Assert.Equal(NoSurface.ContentSize, NativeSize(handle));

        Assert.NotEqual(0, Sdl.PInvoke.SetWindowSize(handle, 500, 400));
        Assert.NotEqual(0, Sdl.PInvoke.SyncWindow(handle));
        AssertSuccess(window.SetMode(WindowMode.BorderlessFullscreen));
        AssertSuccess(window.SetMode(WindowMode.Windowed));

        Assert.False(IsNativeFullscreen(window));
        Assert.Equal(new WindowSize(500, 400), NativeSize(handle));
    }

    [Theory]
    [InlineData("SetMode", "disposed", typeof(ObjectDisposedException))]
    [InlineData("SetMode", "wrong thread", typeof(WrongThreadException))]
    [InlineData("SetMode", "wrong thread and disposed", typeof(WrongThreadException))]
    [InlineData("SetMode exclusive", "disposed", typeof(ObjectDisposedException))]
    [InlineData("SetMode exclusive", "wrong thread", typeof(WrongThreadException))]
    [InlineData("SetExclusiveFullscreen", "disposed", typeof(ObjectDisposedException))]
    [InlineData("SetExclusiveFullscreen", "wrong thread", typeof(WrongThreadException))]
    [InlineData("SetExclusiveFullscreen", "wrong thread and disposed", typeof(WrongThreadException))]
    [InlineData("SetExclusiveFullscreen default", "disposed", typeof(ObjectDisposedException))]
    [InlineData("SetExclusiveFullscreen default", "wrong thread", typeof(WrongThreadException))]
    public void Misuse_Throws(string call, string misuse, Type expected)
    {
        using var variable = NativeEnvironmentVariable.Set("SDL_VIDEO_DRIVER", null);
        using SdlVideoSession session = Start(HeadlessMode.NoSurface);
        using SdlPlatformWindow window = session.CreateWindow(NoSurface).Value;
        DisplayMode current = SdlDisplays.Build().Value[0].CurrentMode;
        SdlWindow handle = Sdl.PInvoke.GetWindowFromID(window.Id.Value);
        bool disposed = misuse.EndsWith("disposed", StringComparison.Ordinal);
        if (disposed)
        {
            window.Dispose();
        }

        // The thread and disposal are checked before the argument, and before any SDL call.
        Action act = call switch
        {
            "SetMode" => () => window.SetMode(WindowMode.BorderlessFullscreen),
            "SetMode exclusive" => () => window.SetMode(WindowMode.ExclusiveFullscreen),
            "SetExclusiveFullscreen" => () => window.SetExclusiveFullscreen(current),
            _ => () => window.SetExclusiveFullscreen(default),
        };
        Exception? thrown = misuse.StartsWith("wrong thread", StringComparison.Ordinal) ? RunOnWorker(act) : Record.Exception(act);

        Assert.IsType(expected, thrown, exactMatch: true);
        if (!disposed)
        {
            Assert.Equal(0, (long)(Sdl.PInvoke.GetWindowFlags(handle) & SdlWindowFlags.Fullscreen));
        }
    }

    [Fact]
    public void SetMode_Succeeding_DoesNotAllocate()
    {
        using var variable = NativeEnvironmentVariable.Set("SDL_VIDEO_DRIVER", null);
        using SdlVideoSession session = Start(HeadlessMode.NoSurface);
        using SdlPlatformWindow window = session.CreateWindow(NoSurface).Value;
        _ = SwitchModes(window);

        long before = GC.GetAllocatedBytesForCurrentThread();
        int failures = 0;
        for (int i = 0; i < 100; i++)
        {
            failures += SwitchModes(window);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, failures);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void ImplementsIWindow()
    {
        using var variable = NativeEnvironmentVariable.Set("SDL_VIDEO_DRIVER", null);
        using SdlVideoSession session = Start(HeadlessMode.NoSurface);
        IWindow window = session.CreateWindow(NoSurface).Value;
        uint id = window.Id.Value;
        SdlWindow handle = Sdl.PInvoke.GetWindowFromID(id);
        try
        {
            // Every member through the interface, against SDL's own state.
            Assert.NotEqual(default, window.Id);
            Assert.Equal(NoSurface.ContentSize, window.Size);
            Assert.Equal(NoSurface.ContentSize, window.PixelSize);
            Assert.Equal(NoSurface.ContentSize, window.ContentSize);
            Assert.Equal(1f, window.PixelDensity);
            Assert.Equal(1f, window.DisplayScale);
            Assert.True(window.State.HasFlag(WindowState.Visible));
            Assert.Equal(WindowMode.Windowed, window.Mode);
            Assert.Equal(new DisplayId(Sdl.PInvoke.GetDisplayForWindow(handle)), window.Display);
            Assert.Null(window.VulkanSurface);

            AssertSuccess(window.SetTitle("Through IWindow"));
            Assert.Equal("Through IWindow", Sdl.PInvoke.GetWindowTitle(handle));
            AssertSuccess(window.SetContentSize(new WindowSize(400, 300)));
            Assert.Equal(new WindowSize(400, 300), NativeSize(handle));
            AssertSuccess(window.StartTextInput());
            Assert.True(window.IsTextInputActive);
            AssertSuccess(window.StopTextInput());
            Assert.False(window.IsTextInputActive);
            AssertSuccess(window.SetRelativeMouseMode(true));
            Assert.True(window.IsRelativeMouseMode);
            AssertSuccess(window.SetMode(WindowMode.BorderlessFullscreen));
            Assert.NotEqual(0, (long)(Sdl.PInvoke.GetWindowFlags(handle) & SdlWindowFlags.Fullscreen));
            Assert.True(window.SetExclusiveFullscreen(SdlDisplays.Build().Value[0].CurrentMode).TryGetError(out EngineError error));
            Assert.Equal(ErrorKind.NotFound, error.Kind);
        }
        finally
        {
            window.Dispose();
        }

        Assert.True(Sdl.PInvoke.GetWindowFromID(id).IsNull());
        _ = Assert.Throws<ObjectDisposedException>(() => window.Mode);
        _ = Assert.Throws<ObjectDisposedException>(() => window.SetMode(WindowMode.Windowed));
        window.Dispose();
    }

    private static int SwitchModes(SdlPlatformWindow window)
    {
        int failures = window.SetMode(WindowMode.BorderlessFullscreen).IsSuccess ? 0 : 1;
        failures += window.SetMode(WindowMode.BorderlessFullscreen).IsSuccess ? 0 : 1;
        failures += window.SetMode(WindowMode.Windowed).IsSuccess ? 0 : 1;
        failures += window.SetMode(WindowMode.Windowed).IsSuccess ? 0 : 1;
        return failures;
    }

    private static void AssertSuccess(Result result) => Assert.True(result.IsSuccess, result.ToString());

    private static SdlVideoSession Start(HeadlessMode headless)
    {
        PlatformOptions options = new() { Organization = "BrutalEngine", Application = "Platform.Sdl3.Tests", Headless = headless };
        return SdlVideoSession.Start(options, null).Value;
    }

    private static bool IsNativeFullscreen(SdlPlatformWindow window) =>
        (Sdl.PInvoke.GetWindowFlags(Sdl.PInvoke.GetWindowFromID(window.Id.Value)) & SdlWindowFlags.Fullscreen) != 0;

    private static WindowSize NativeSize(SdlPlatformWindow window) => NativeSize(Sdl.PInvoke.GetWindowFromID(window.Id.Value));

    private static WindowSize NativeSize(SdlWindow handle)
    {
        int width, height;

        // Sound: both ints are locals of this frame, which outlives the call.
        Assert.NotEqual(0, Sdl.PInvoke.GetWindowSize(handle, &width, &height));
        return new WindowSize(width, height);
    }

    // Runs an action on a worker thread with a bounded wait and returns what it threw.
    private static Exception? RunOnWorker(Action action)
    {
        Exception? thrown = null;
        using ManualResetEventSlim done = new();
        Thread worker = new(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                thrown = exception;
            }
            finally
            {
                done.Set();
            }
        })
        {
            IsBackground = true,
        };

        worker.Start();
        Assert.True(done.Wait(TimeSpan.FromSeconds(WorkerTimeoutSeconds), TestContext.Current.CancellationToken), "The worker thread didn't finish within 30 s.");
        Assert.True(worker.Join(TimeSpan.FromSeconds(WorkerTimeoutSeconds)));
        return thrown;
    }
}
