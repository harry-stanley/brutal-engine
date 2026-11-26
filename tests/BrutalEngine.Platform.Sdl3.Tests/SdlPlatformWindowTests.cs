using System;
using System.Threading;
using Brutal.SdlApi;
using BrutalEngine.Core;
using Xunit;

namespace BrutalEngine.Platform.Sdl3.Tests;

[Collection(SdlNativeTests.Name)]
public sealed unsafe class SdlPlatformWindowTests
{
    private const int WorkerTimeoutSeconds = 30;

    // Every member that checks the thread and disposal, by name (Use calls it).
    public static TheoryData<string> Members { get; } = new(
        "Id", "Size", "PixelSize", "ContentSize", "PixelDensity", "DisplayScale", "State", "Mode", "Display", "IsTextInputActive",
        "IsRelativeMouseMode", "VulkanSurface", "RefreshSnapshot");

    private static WindowOptions NoSurface { get; } = new() { Title = "Platform.Sdl3.Tests", ContentSize = new(320, 200), Surface = WindowSurface.None };

    [Fact]
    public void Create_NoSurfaceOnDummy_HasSnapshot()
    {
        using var variable = NativeEnvironmentVariable.Set("SDL_VIDEO_DRIVER", null);
        using SdlVideoSession session = Start(HeadlessMode.NoSurface);
        using SdlPlatformWindow window = session.CreateWindow(NoSurface).Value;
        uint id = window.Id.Value;
        SdlWindow handle = Sdl.PInvoke.GetWindowFromID(id);
        Assert.False(handle.IsNull(), Sdl.PInvoke.GetError());

        // The dummy driver's display is at scale 1, so all three sizes equal the request.
        Assert.Equal(NoSurface.ContentSize, window.ContentSize);
        Assert.Equal(NativeSize(handle), window.Size);
        Assert.Equal(NativePixelSize(handle), window.PixelSize);
        Assert.Equal(NoSurface.ContentSize, window.PixelSize);
        Assert.Equal(Sdl.PInvoke.GetWindowDisplayScale(handle), window.DisplayScale);
        Assert.Equal(Sdl.PInvoke.GetWindowPixelDensity(handle), window.PixelDensity);
        Assert.Equal(new DisplayId(Sdl.PInvoke.GetDisplayForWindow(handle)), window.Display);
        Assert.Equal(SdlPlatformWindow.StateFrom(Sdl.PInvoke.GetWindowFlags(handle)), window.State);
        Assert.Equal(WindowMode.Windowed, window.Mode);
        Assert.Equal(Sdl.PInvoke.TextInputActive(handle) != 0, window.IsTextInputActive);
        Assert.False(window.IsRelativeMouseMode);
        Assert.Null(window.VulkanSurface);

        window.Dispose();

        Assert.True(window.IsDisposed);
        Assert.True(Sdl.PInvoke.GetWindowFromID(id).IsNull());
        Assert.Equal(0, WindowCount());
    }

    [Theory]
    [InlineData(true, true, WindowMode.Windowed)]
    [InlineData(false, false, WindowMode.Windowed)]
    [InlineData(true, false, WindowMode.BorderlessFullscreen)]
    [InlineData(false, true, WindowMode.BorderlessFullscreen)]
    public void Create_Options_SetWindowFlags(bool resizable, bool highPixelDensity, WindowMode mode)
    {
        using var variable = NativeEnvironmentVariable.Set("SDL_VIDEO_DRIVER", null);
        using SdlVideoSession session = Start(HeadlessMode.NoSurface);
        WindowOptions options = NoSurface with { Resizable = resizable, HighPixelDensity = highPixelDensity, Mode = mode };

        using SdlPlatformWindow window = session.CreateWindow(options).Value;

        SdlWindowFlags flags = Sdl.PInvoke.GetWindowFlags(Sdl.PInvoke.GetWindowFromID(window.Id.Value));
        Assert.Equal(resizable, (flags & SdlWindowFlags.Resizable) != 0);
        Assert.Equal(highPixelDensity, (flags & SdlWindowFlags.HighPixelDensity) != 0);
        Assert.Equal(mode == WindowMode.BorderlessFullscreen, (flags & SdlWindowFlags.Fullscreen) != 0);
        Assert.Equal(0, (long)(flags & SdlWindowFlags.Vulkan));
        Assert.Equal(mode, window.Mode);
    }

    [Fact]
    public void Create_WrongEstimate_ResizesOnce()
    {
        using var variable = NativeEnvironmentVariable.Set("SDL_VIDEO_DRIVER", null);
        using SdlVideoSession session = Start(HeadlessMode.NoSurface);

        // A display content scale of 2 makes the estimate 640 by 400; the window's own scale on dummy is 1.
        using SdlPlatformWindow window = SdlPlatformWindow.Create(NoSurface, session.VideoDriver, session.Headless, estimateScale: 2f).Value;

        Assert.Equal(1f, window.DisplayScale);
        Assert.Equal(NoSurface.ContentSize, window.Size);
        Assert.Equal(NoSurface.ContentSize, window.ContentSize);
        Assert.Equal(NoSurface.ContentSize, NativeSize(Sdl.PInvoke.GetWindowFromID(window.Id.Value)));
    }

    [Fact]
    public void Create_VulkanOnNoSurface_IsUnsupportedWithoutSdlCall()
    {
        using var variable = NativeEnvironmentVariable.Set("SDL_VIDEO_DRIVER", null);
        using SdlVideoSession session = Start(HeadlessMode.NoSurface);
        Sdl.PInvoke.ClearError();

        Result<SdlPlatformWindow> result = session.CreateWindow(NoSurface with { Surface = WindowSurface.Vulkan });
        DisposeIfCreated(result);

        Assert.True(result.TryGetError(out EngineError error));
        Assert.Equal(ErrorKind.Unsupported, error.Kind);
        Assert.Equal(SdlPlatformWindow.NoSurfaceVulkanMessage, error.Message);

        // SDL_CreateWindowWithProperties with SDL_WINDOW_VULKAN on dummy fails and sets SDL's error text.
        Assert.True(string.IsNullOrEmpty(Sdl.PInvoke.GetError()), Sdl.PInvoke.GetError());
        Assert.Equal(0, WindowCount());
    }

    [Fact]
    public void Create_MissingDisplay_IsNotFound()
    {
        using var variable = NativeEnvironmentVariable.Set("SDL_VIDEO_DRIVER", null);
        using SdlVideoSession session = Start(HeadlessMode.NoSurface);

        Result<SdlPlatformWindow> result = session.CreateWindow(NoSurface with { Display = new DisplayId(uint.MaxValue) });
        DisposeIfCreated(result);

        Assert.True(result.TryGetError(out EngineError error));
        Assert.Equal(ErrorKind.NotFound, error.Kind);
        Assert.Contains("4294967295", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, WindowCount());
    }

    [Fact]
    public void Create_ConnectedDisplay_OpensOnIt()
    {
        using var variable = NativeEnvironmentVariable.Set("SDL_VIDEO_DRIVER", null);
        using SdlVideoSession session = Start(HeadlessMode.NoSurface);
        DisplayId primary = new(Sdl.PInvoke.GetPrimaryDisplay());
        Assert.NotEqual(default, primary);

        using SdlPlatformWindow window = session.CreateWindow(NoSurface with { Display = primary }).Value;

        Assert.Equal(primary, window.Display);
    }

    [Theory]
    [InlineData("null options", typeof(ArgumentNullException))]
    [InlineData("exclusive fullscreen", typeof(ArgumentException))]
    [InlineData("side too large", typeof(ArgumentOutOfRangeException))]
    public void Create_InvalidOptions_ThrowsWithoutWindow(string row, Type expected)
    {
        using var variable = NativeEnvironmentVariable.Set("SDL_VIDEO_DRIVER", null);
        using SdlVideoSession session = Start(HeadlessMode.NoSurface);
        WindowOptions? options = row switch
        {
            "null options" => null,
            "exclusive fullscreen" => NoSurface with { Mode = WindowMode.ExclusiveFullscreen },
            "side too large" => NoSurface with { ContentSize = new(16385, 200) },
            _ => throw new ArgumentOutOfRangeException(nameof(row), row, "Unknown row."),
        };
        Result<SdlPlatformWindow> result = default;

        Exception? thrown = Record.Exception(() => result = session.CreateWindow(options!));
        DisposeIfCreated(result);

        Assert.IsType(expected, thrown, exactMatch: true);
        Assert.Equal(0, WindowCount());
    }

    [Fact]
    public void CreateWindow_DisposedSession_Throws()
    {
        using var variable = NativeEnvironmentVariable.Set("SDL_VIDEO_DRIVER", null);
        SdlVideoSession session = Start(HeadlessMode.NoSurface);
        session.Dispose();

        _ = Assert.Throws<ObjectDisposedException>(() => session.CreateWindow(NoSurface));
    }

    [Fact]
    public void RefreshSnapshot_AfterNativeChanges_ReadsThem()
    {
        using var variable = NativeEnvironmentVariable.Set("SDL_VIDEO_DRIVER", null);
        using SdlVideoSession session = Start(HeadlessMode.NoSurface);
        using SdlPlatformWindow window = session.CreateWindow(NoSurface).Value;
        SdlWindow handle = Sdl.PInvoke.GetWindowFromID(window.Id.Value);
        Assert.NotEqual(0, Sdl.PInvoke.SetWindowSize(handle, 400, 300));
        Assert.NotEqual(0, Sdl.PInvoke.StartTextInput(handle));
        Assert.NotEqual(0, Sdl.PInvoke.SetWindowRelativeMouseMode(handle, 1));

        // The getters are a snapshot: nothing changes before the refresh.
        Assert.Equal(NoSurface.ContentSize, window.Size);
        Assert.False(window.IsTextInputActive);
        Assert.False(window.IsRelativeMouseMode);

        Result refreshed = window.RefreshSnapshot();

        Assert.True(refreshed.IsSuccess, refreshed.ToString());
        Assert.Equal(new WindowSize(400, 300), window.Size);
        Assert.Equal(new WindowSize(400, 300), window.PixelSize);
        Assert.Equal(new WindowSize(400, 300), window.ContentSize);
        Assert.True(window.IsTextInputActive);
        Assert.True(window.IsRelativeMouseMode);
    }

    [Fact]
    public void Getters_DoNotAllocate()
    {
        using var variable = NativeEnvironmentVariable.Set("SDL_VIDEO_DRIVER", null);
        using SdlVideoSession session = Start(HeadlessMode.NoSurface);
        using SdlPlatformWindow window = session.CreateWindow(NoSurface).Value;
        _ = ReadGetters(window);

        long before = GC.GetAllocatedBytesForCurrentThread();
        long total = 0;
        for (int i = 0; i < 1_000; i++)
        {
            total += ReadGetters(window);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(total > 0);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void RefreshSnapshot_DoesNotAllocate()
    {
        using var variable = NativeEnvironmentVariable.Set("SDL_VIDEO_DRIVER", null);
        using SdlVideoSession session = Start(HeadlessMode.NoSurface);
        using SdlPlatformWindow window = session.CreateWindow(NoSurface).Value;
        Assert.True(window.RefreshSnapshot().IsSuccess);

        long before = GC.GetAllocatedBytesForCurrentThread();
        int failures = 0;
        for (int i = 0; i < 1_000; i++)
        {
            failures += window.RefreshSnapshot().IsSuccess ? 0 : 1;
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, failures);
        Assert.Equal(0, allocated);
    }

    [Theory]
    [MemberData(nameof(Members))]
    public void AfterDispose_MembersThrow(string member)
    {
        using var variable = NativeEnvironmentVariable.Set("SDL_VIDEO_DRIVER", null);
        using SdlVideoSession session = Start(HeadlessMode.NoSurface);
        SdlPlatformWindow window = session.CreateWindow(NoSurface).Value;
        window.Dispose();

        ObjectDisposedException thrown = Assert.Throws<ObjectDisposedException>(() => Use(window, member));

        Assert.Equal(typeof(SdlPlatformWindow).FullName, thrown.ObjectName);
    }

    [Fact]
    public void Dispose_Twice_DoesNothing()
    {
        using var variable = NativeEnvironmentVariable.Set("SDL_VIDEO_DRIVER", null);
        using SdlVideoSession session = Start(HeadlessMode.NoSurface);
        SdlPlatformWindow window = session.CreateWindow(NoSurface).Value;
        window.Dispose();
        using SdlPlatformWindow other = session.CreateWindow(NoSurface).Value;

        window.Dispose();

        Assert.True(window.IsDisposed);
        Assert.False(Sdl.PInvoke.GetWindowFromID(other.Id.Value).IsNull());
        Assert.Equal(1, WindowCount());
    }

    [Theory]
    [MemberData(nameof(Members))]
    [InlineData("Dispose")]
    [InlineData("disposed Size")]
    [InlineData("disposed Dispose")]
    public void FromOtherThread_Throws(string member)
    {
        using var variable = NativeEnvironmentVariable.Set("SDL_VIDEO_DRIVER", null);
        using SdlVideoSession session = Start(HeadlessMode.NoSurface);
        using SdlPlatformWindow window = session.CreateWindow(NoSurface).Value;
        uint id = window.Id.Value;
        const string Disposed = "disposed ";
        bool disposed = member.StartsWith(Disposed, StringComparison.Ordinal);
        if (disposed)
        {
            // The thread is checked before disposal, so a disposed window still reports the wrong thread.
            window.Dispose();
            member = member[Disposed.Length..];
        }

        Exception? thrown = RunOnWorker(() => Use(window, member));

        WrongThreadException wrongThread = Assert.IsType<WrongThreadException>(thrown, exactMatch: true);
        Assert.Equal(Environment.CurrentManagedThreadId, wrongThread.ExpectedThreadId);
        Assert.Equal(disposed, window.IsDisposed);
        Assert.Equal(disposed, Sdl.PInvoke.GetWindowFromID(id).IsNull());
    }

    [Theory]
    [InlineData(SdlWindowFlags.Hidden, WindowState.None)]
    [InlineData((SdlWindowFlags)0, WindowState.Visible)]
    [InlineData(SdlWindowFlags.Minimized, WindowState.Visible | WindowState.Minimized)]
    [InlineData(SdlWindowFlags.Maximized, WindowState.Visible | WindowState.Maximized)]
    [InlineData(SdlWindowFlags.InputFocus, WindowState.Visible | WindowState.InputFocus)]
    [InlineData(SdlWindowFlags.MouseFocus, WindowState.Visible | WindowState.MouseFocus)]
    [InlineData(SdlWindowFlags.Occluded, WindowState.Visible | WindowState.Occluded)]
    [InlineData(SdlWindowFlags.Hidden | SdlWindowFlags.Minimized | SdlWindowFlags.Occluded, WindowState.Minimized | WindowState.Occluded)]
    [InlineData(SdlWindowFlags.Resizable | SdlWindowFlags.Vulkan | SdlWindowFlags.Fullscreen, WindowState.Visible)]
    public void StateFrom_Flags_MapsEachStateFlag(SdlWindowFlags flags, WindowState expected)
    {
        Assert.Equal(expected, SdlPlatformWindow.StateFrom(flags));
    }

    [Fact]
    [Trait("Category", "Gpu")]
    public void Create_OffscreenVulkan_OnLinux_Succeeds()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux only: Vulkan drivers there offer VK_EXT_headless_surface.");
        using var variable = NativeEnvironmentVariable.Set("SDL_VIDEO_DRIVER", null);
        using SdlVideoSession session = Start(HeadlessMode.Offscreen);
        SdlPlatformWindow window = session.CreateWindow(NoSurface with { Surface = WindowSurface.Vulkan }).Value;
        SdlVulkanSurfaceSource source;
        try
        {
            SdlVulkanSurfaceSource? created = window.VulkanSurface;
            Assert.NotNull(created);
            source = created;
            Assert.True(HeadlessSurfaceRule.ContainsExtension(source, "VK_EXT_headless_surface"u8));
            Assert.NotEqual(0, (long)(Sdl.PInvoke.GetWindowFlags(Sdl.PInvoke.GetWindowFromID(window.Id.Value)) & SdlWindowFlags.Vulkan));
        }
        finally
        {
            window.Dispose();
        }

        // The source lives exactly as long as its window.
        _ = Assert.Throws<ObjectDisposedException>(() => source.RequiredInstanceExtensionCount);
    }

    [Fact]
    [Trait("Category", "Gpu")]
    public void Create_OffscreenVulkan_OnWindows_IsUnsupported()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only: no Windows Vulkan driver offers VK_EXT_headless_surface.");
        using var variable = NativeEnvironmentVariable.Set("SDL_VIDEO_DRIVER", null);
        using SdlVideoSession session = Start(HeadlessMode.Offscreen);

        Result<SdlPlatformWindow> result = session.CreateWindow(NoSurface with { Surface = WindowSurface.Vulkan });
        DisposeIfCreated(result);

        Assert.True(result.TryGetError(out EngineError error));
        Assert.Equal(ErrorKind.Unsupported, error.Kind);
        Assert.Equal(HeadlessSurfaceRule.Message, error.Message);
        Assert.Equal(0, WindowCount());
    }

    private static SdlVideoSession Start(HeadlessMode headless)
    {
        PlatformOptions options = new() { Organization = "BrutalEngine", Application = "Platform.Sdl3.Tests", Headless = headless };
        return SdlVideoSession.Start(options, null).Value;
    }

    // A regression that returns a window where the test expects a failure must not leak it into later tests.
    private static void DisposeIfCreated(Result<SdlPlatformWindow> result)
    {
        if (result.TryGetValue(out SdlPlatformWindow? window))
        {
            window.Dispose();
        }
    }

    // Every getter, with no allocation of its own.
    private static long ReadGetters(SdlPlatformWindow window)
    {
        long total = window.Id.Value + window.Size.Width + window.PixelSize.Height + window.ContentSize.Width + window.Display.Value;
        total += (long)(window.PixelDensity + window.DisplayScale) + (int)window.State + (int)window.Mode;
        total += (window.IsTextInputActive ? 1 : 0) + (window.IsRelativeMouseMode ? 1 : 0) + (window.VulkanSurface is null ? 0 : 1);
        return total;
    }

    // Calls one member by name. Boxes its value: only for the misuse tests, which measure nothing.
    private static object? Use(SdlPlatformWindow window, string member) => member switch
    {
        "Id" => window.Id,
        "Size" => window.Size,
        "PixelSize" => window.PixelSize,
        "ContentSize" => window.ContentSize,
        "PixelDensity" => window.PixelDensity,
        "DisplayScale" => window.DisplayScale,
        "State" => window.State,
        "Mode" => window.Mode,
        "Display" => window.Display,
        "IsTextInputActive" => window.IsTextInputActive,
        "IsRelativeMouseMode" => window.IsRelativeMouseMode,
        "VulkanSurface" => window.VulkanSurface,
        "RefreshSnapshot" => window.RefreshSnapshot().IsSuccess,
        "Dispose" => DisposeWindow(window),
        _ => throw new ArgumentOutOfRangeException(nameof(member), member, "Unknown member."),
    };

    private static bool DisposeWindow(SdlPlatformWindow window)
    {
        window.Dispose();
        return true;
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

    private static WindowSize NativeSize(SdlWindow handle)
    {
        int width, height;

        // Sound: both ints are locals of this frame, which outlives the call.
        Assert.NotEqual(0, Sdl.PInvoke.GetWindowSize(handle, &width, &height));
        return new WindowSize(width, height);
    }

    private static WindowSize NativePixelSize(SdlWindow handle)
    {
        int width, height;

        // Sound: both ints are locals of this frame, which outlives the call.
        Assert.NotEqual(0, Sdl.PInvoke.GetWindowSizeInPixels(handle, &width, &height));
        return new WindowSize(width, height);
    }

    private static int WindowCount()
    {
        int count;

        // Sound: count is a local of this frame; SDL allocates the returned array for the caller, freed here unread.
        SdlWindow* windows = Sdl.PInvoke.GetWindows(&count);
        Assert.True(windows is not null, Sdl.PInvoke.GetError());
        Sdl.PInvoke.Free(windows);
        return count;
    }
}
