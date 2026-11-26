using System;
using System.Threading;
using Brutal.SdlApi;
using BrutalEngine.Core;
using Xunit;

namespace BrutalEngine.Platform.Sdl3.Tests;

[Collection(SdlNativeTests.Name)]
public sealed unsafe class SdlPlatformWindowSetterTests
{
    private const int WorkerTimeoutSeconds = 30;
    private const string Disposed = "disposed";
    private const string WrongThread = "wrong thread";
    private const string WrongThreadAndDisposed = "wrong thread and disposed";

    // Longer than BRUTAL's 256-byte stack buffer for UTF-8 strings, so its marshaller takes the native-memory path.
    private static readonly string _longTitle = new('t', 300);

    private static WindowOptions NoSurface { get; } = new() { Title = "Platform.Sdl3.Tests", ContentSize = new(320, 200), Surface = WindowSurface.None };

    public static TheoryData<string> Setters { get; } = new(
        "SetTitle", "SetContentSize", "StartTextInput", "StopTextInput", "SetRelativeMouseMode on", "SetRelativeMouseMode off");

    // Each setter's misuse rows: disposal, the wrong thread, and the thread checked before disposal. The argument rows
    // pin that the thread and disposal are checked before the argument.
    public static TheoryData<string, string, Type> MisuseRows { get; } = BuildMisuseRows();

    [Theory]
    [MemberData(nameof(Setters))]
    public void Setters_ReturnResults(string setter)
    {
        using var variable = NativeEnvironmentVariable.Set("SDL_VIDEO_DRIVER", null);
        using SdlVideoSession session = Start(HeadlessMode.NoSurface);
        using SdlPlatformWindow window = session.CreateWindow(NoSurface).Value;
        SdlWindow handle = Sdl.PInvoke.GetWindowFromID(window.Id.Value);
        switch (setter)
        {
            case "SetTitle":
                // Not ASCII, so the title must reach SDL as UTF-8.
                AssertSuccess(window.SetTitle("Setter title \u00e9\u2713"));
                Assert.Equal("Setter title \u00e9\u2713", Sdl.PInvoke.GetWindowTitle(handle));
                break;

            case "SetContentSize":
                AssertSuccess(window.SetContentSize(new WindowSize(400, 300)));

                // The dummy driver's display is at scale 1, so the window coordinates equal the content size. Dummy
                // applies the size at once, so the next refresh reads it; the snapshot doesn't change before that.
                Assert.Equal(new WindowSize(400, 300), NativeSize(handle));
                Assert.Equal(NoSurface.ContentSize, window.ContentSize);
                AssertSuccess(window.RefreshSnapshot());
                Assert.Equal(new WindowSize(400, 300), window.Size);
                Assert.Equal(new WindowSize(400, 300), window.ContentSize);
                break;

            case "StartTextInput":
                Assert.False(window.IsTextInputActive);
                AssertSuccess(window.StartTextInput());

                // Text input has no OS event: the getter changes as soon as the setter succeeds.
                Assert.True(window.IsTextInputActive);
                Assert.NotEqual(0, Sdl.PInvoke.TextInputActive(handle));
                AssertSuccess(window.RefreshSnapshot());
                Assert.True(window.IsTextInputActive);
                break;

            case "StopTextInput":
                Assert.NotEqual(0, Sdl.PInvoke.StartTextInput(handle));
                AssertSuccess(window.RefreshSnapshot());
                Assert.True(window.IsTextInputActive);
                AssertSuccess(window.StopTextInput());
                Assert.False(window.IsTextInputActive);
                Assert.Equal(0, Sdl.PInvoke.TextInputActive(handle));
                AssertSuccess(window.RefreshSnapshot());
                Assert.False(window.IsTextInputActive);
                break;

            case "SetRelativeMouseMode on":
                Assert.False(window.IsRelativeMouseMode);
                AssertSuccess(window.SetRelativeMouseMode(true));

                // Relative mouse mode has no OS event either.
                Assert.True(window.IsRelativeMouseMode);
                Assert.NotEqual(0, Sdl.PInvoke.GetWindowRelativeMouseMode(handle));
                AssertSuccess(window.RefreshSnapshot());
                Assert.True(window.IsRelativeMouseMode);
                break;

            case "SetRelativeMouseMode off":
                Assert.NotEqual(0, Sdl.PInvoke.SetWindowRelativeMouseMode(handle, 1));
                AssertSuccess(window.RefreshSnapshot());
                Assert.True(window.IsRelativeMouseMode);
                AssertSuccess(window.SetRelativeMouseMode(false));
                Assert.False(window.IsRelativeMouseMode);
                Assert.Equal(0, Sdl.PInvoke.GetWindowRelativeMouseMode(handle));
                AssertSuccess(window.RefreshSnapshot());
                Assert.False(window.IsRelativeMouseMode);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(setter), setter, "Unknown setter.");
        }
    }

    [Fact]
    public void RefusedSetter_ReturnsFailedResult()
    {
        // SDL's offscreen driver has no relative mouse mode (observed on Windows and Linux); the dummy driver accepts it.
        using var variable = NativeEnvironmentVariable.Set("SDL_VIDEO_DRIVER", null);
        using SdlVideoSession session = Start(HeadlessMode.Offscreen);
        using SdlPlatformWindow window = session.CreateWindow(NoSurface).Value;
        Sdl.PInvoke.ClearError();

        Result result = window.SetRelativeMouseMode(true);

        Assert.True(result.TryGetError(out EngineError error));
        Assert.Equal(ErrorKind.Native, error.Kind);
        Assert.Equal("SDL_SetWindowRelativeMouseMode failed: That operation is not supported", error.Message);

        // A refused setter leaves the flag as it was.
        Assert.False(window.IsRelativeMouseMode);
        AssertSuccess(window.RefreshSnapshot());
        Assert.False(window.IsRelativeMouseMode);
    }

    [Theory]
    [MemberData(nameof(MisuseRows))]
    public void Misuse_Throws(string setter, string misuse, Type expected)
    {
        using var variable = NativeEnvironmentVariable.Set("SDL_VIDEO_DRIVER", null);
        using SdlVideoSession session = Start(HeadlessMode.NoSurface);
        using SdlPlatformWindow window = session.CreateWindow(NoSurface).Value;
        SdlWindow handle = Sdl.PInvoke.GetWindowFromID(window.Id.Value);
        bool disposed = misuse.Contains(Disposed, StringComparison.Ordinal);
        bool otherThread = misuse.StartsWith(WrongThread, StringComparison.Ordinal);
        if (disposed)
        {
            window.Dispose();
        }

        Exception? thrown = otherThread ? RunOnWorker(() => Call(window, setter, misuse)) : Record.Exception(() => Call(window, setter, misuse));

        Assert.IsType(expected, thrown, exactMatch: true);
        switch (thrown)
        {
            case WrongThreadException wrongThread:
                Assert.Equal(Environment.CurrentManagedThreadId, wrongThread.ExpectedThreadId);
                break;
            case ObjectDisposedException objectDisposed:
                Assert.Equal(typeof(SdlPlatformWindow).FullName, objectDisposed.ObjectName);
                break;
            case ArgumentException argument:
                Assert.Equal(setter == "SetTitle" ? "title" : "contentSize", argument.ParamName);
                break;
        }

        if (!disposed)
        {
            // Misuse throws before any effect: SDL's window keeps its creation state.
            Assert.Equal(NoSurface.Title, Sdl.PInvoke.GetWindowTitle(handle));
            Assert.Equal(NoSurface.ContentSize, NativeSize(handle));
            Assert.Equal(0, Sdl.PInvoke.TextInputActive(handle));
            Assert.Equal(0, Sdl.PInvoke.GetWindowRelativeMouseMode(handle));
        }
    }

    [Fact]
    public void Setters_Succeeding_DoNotAllocate()
    {
        using var variable = NativeEnvironmentVariable.Set("SDL_VIDEO_DRIVER", null);
        using SdlVideoSession session = Start(HeadlessMode.NoSurface);
        using SdlPlatformWindow window = session.CreateWindow(NoSurface).Value;
        WindowSize small = new(320, 200);
        WindowSize large = new(400, 300);
        _ = CallEverySetter(window, small, large);

        long before = GC.GetAllocatedBytesForCurrentThread();
        int failures = 0;
        for (int i = 0; i < 100; i++)
        {
            failures += CallEverySetter(window, small, large);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, failures);
        Assert.Equal(0, allocated);
    }

    private static TheoryData<string, string, Type> BuildMisuseRows()
    {
        TheoryData<string, string, Type> rows = [];
        foreach (string setter in new[] { "SetTitle", "SetContentSize", "StartTextInput", "StopTextInput", "SetRelativeMouseMode" })
        {
            rows.Add(setter, Disposed, typeof(ObjectDisposedException));
            rows.Add(setter, WrongThread, typeof(WrongThreadException));
            rows.Add(setter, WrongThreadAndDisposed, typeof(WrongThreadException));
        }

        rows.Add("SetTitle", "null title", typeof(ArgumentNullException));
        rows.Add("SetTitle", "disposed, null title", typeof(ObjectDisposedException));
        rows.Add("SetTitle", "wrong thread, null title", typeof(WrongThreadException));
        rows.Add("SetContentSize", "width 0", typeof(ArgumentOutOfRangeException));
        rows.Add("SetContentSize", "width -1", typeof(ArgumentOutOfRangeException));
        rows.Add("SetContentSize", "width 16385", typeof(ArgumentOutOfRangeException));
        rows.Add("SetContentSize", "height 0", typeof(ArgumentOutOfRangeException));
        rows.Add("SetContentSize", "height -1", typeof(ArgumentOutOfRangeException));
        rows.Add("SetContentSize", "height 16385", typeof(ArgumentOutOfRangeException));
        rows.Add("SetContentSize", "disposed, width 0", typeof(ObjectDisposedException));
        rows.Add("SetContentSize", "wrong thread, width 0", typeof(WrongThreadException));
        return rows;
    }

    // Calls one setter by name with the argument its misuse row names; a valid argument otherwise.
    private static Result Call(SdlPlatformWindow window, string setter, string misuse)
    {
        string argument = misuse.Contains(", ", StringComparison.Ordinal) ? misuse[(misuse.IndexOf(", ", StringComparison.Ordinal) + 2)..] : misuse;
        return setter switch
        {
            "SetTitle" => window.SetTitle(argument == "null title" ? null! : "Renamed"),
            "SetContentSize" => window.SetContentSize(argument switch
            {
                "width 0" => new WindowSize(0, 200),
                "width -1" => new WindowSize(-1, 200),
                "width 16385" => new WindowSize(16385, 200),
                "height 0" => new WindowSize(400, 0),
                "height -1" => new WindowSize(400, -1),
                "height 16385" => new WindowSize(400, 16385),
                _ => new WindowSize(400, 300),
            }),
            "StartTextInput" => window.StartTextInput(),
            "StopTextInput" => window.StopTextInput(),
            "SetRelativeMouseMode" => window.SetRelativeMouseMode(true),
            _ => throw new ArgumentOutOfRangeException(nameof(setter), setter, "Unknown setter."),
        };
    }

    // Every setter once, each to a value that differs from the last call's; returns how many failed.
    private static int CallEverySetter(SdlPlatformWindow window, WindowSize small, WindowSize large)
    {
        int failures = window.SetTitle("Allocation").IsSuccess ? 0 : 1;
        failures += window.SetTitle(_longTitle).IsSuccess ? 0 : 1;
        failures += window.SetContentSize(large).IsSuccess ? 0 : 1;
        failures += window.SetContentSize(small).IsSuccess ? 0 : 1;
        failures += window.StartTextInput().IsSuccess ? 0 : 1;
        failures += window.StopTextInput().IsSuccess ? 0 : 1;
        failures += window.SetRelativeMouseMode(true).IsSuccess ? 0 : 1;
        failures += window.SetRelativeMouseMode(false).IsSuccess ? 0 : 1;
        return failures;
    }

    private static void AssertSuccess(Result result) => Assert.True(result.IsSuccess, result.ToString());

    private static SdlVideoSession Start(HeadlessMode headless)
    {
        PlatformOptions options = new() { Organization = "BrutalEngine", Application = "Platform.Sdl3.Tests", Headless = headless };
        return SdlVideoSession.Start(options, null).Value;
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
}
