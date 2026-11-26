using System;
using System.IO;
using System.Text;
using System.Threading;
using Brutal.SdlApi;
using BrutalEngine.Core;
using BrutalEngine.Core.Logging;
using BrutalEngine.Core.Logging.Sinks;
using Xunit;

namespace BrutalEngine.Platform.Sdl3.Tests;

[Collection(SdlNativeTests.Name)]
public sealed class SdlVideoSessionTests
{
    private const string VideoDriverVariable = "SDL_VIDEO_DRIVER";

    private static PlatformOptions Options(HeadlessMode headless, Logger? logger = null) =>
        new() { Organization = "BrutalEngine", Application = "Platform.Sdl3.Tests", Headless = headless, Logger = logger };

    [Fact]
    public void Start_NoSurface_OnWorkerThread_UsesDummy()
    {
        string? driver = null;
        HeadlessMode headless = HeadlessMode.None;
        bool startedVideo = false;
        Exception? failure = null;
        Thread worker = new(() =>
        {
            try
            {
                using var variable = NativeEnvironmentVariable.Set(VideoDriverVariable, null);
                using SdlVideoSession session = SdlVideoSession.Start(Options(HeadlessMode.NoSurface), null).Value;
                driver = session.VideoDriver;
                headless = session.Headless;
                startedVideo = Sdl.PInvoke.WasInit(SdlInitFlags.Video) == SdlInitFlags.Video;
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        })
        {
            // A background thread, so a worker hung in SDL can't keep the test host alive after the bounded join.
            IsBackground = true,
        };

        worker.Start();

        // Bounded, so a hang inside SDL fails the test instead of hanging the test host.
        Assert.True(worker.Join(TimeSpan.FromSeconds(30)), "The worker thread didn't finish within 30 s.");
        Assert.Null(failure);
        Assert.Equal("dummy", driver);
        Assert.Equal(HeadlessMode.NoSurface, headless);
        Assert.True(startedVideo);
        Assert.Equal((SdlInitFlags)0, Sdl.PInvoke.WasInit(SdlInitFlags.Video));
    }

    [Fact]
    public void Start_EnvironmentForcesDummy_OffscreenRequested_IsUnsupported()
    {
        using var variable = NativeEnvironmentVariable.Set(VideoDriverVariable, "dummy");

        Result<SdlVideoSession> result = SdlVideoSession.Start(Options(HeadlessMode.Offscreen), null);

        Assert.True(result.TryGetError(out EngineError error));
        Assert.Equal(ErrorKind.Unsupported, error.Kind);
        Assert.Contains("'dummy'", error.Message, StringComparison.Ordinal);
        Assert.Contains("SDL_VIDEO_DRIVER", error.Message, StringComparison.Ordinal);
        Assert.Equal((SdlInitFlags)0, Sdl.PInvoke.WasInit(SdlInitFlags.Video));
    }

    [Fact]
    public void Start_EnvironmentForcesDummy_NoneRequested_ReportsNoSurface()
    {
        using var variable = NativeEnvironmentVariable.Set(VideoDriverVariable, "dummy");

        using SdlVideoSession session = SdlVideoSession.Start(Options(HeadlessMode.None), null).Value;

        Assert.Equal("dummy", session.VideoDriver);
        Assert.Equal(HeadlessMode.NoSurface, session.Headless);
    }

    [Fact]
    public void Start_SecondLiveSession_Throws()
    {
        using var variable = NativeEnvironmentVariable.Set(VideoDriverVariable, null);
        using SdlVideoSession first = SdlVideoSession.Start(Options(HeadlessMode.NoSurface), null).Value;

        _ = Assert.Throws<InvalidOperationException>(() => SdlVideoSession.Start(Options(HeadlessMode.NoSurface), null));
        Assert.Equal(SdlInitFlags.Video, Sdl.PInvoke.WasInit(SdlInitFlags.Video));
    }

    [Fact]
    public void Start_AfterDispose_Succeeds()
    {
        using var variable = NativeEnvironmentVariable.Set(VideoDriverVariable, null);
        SdlVideoSession.Start(Options(HeadlessMode.NoSurface), null).Value.Dispose();
        Assert.Equal((SdlInitFlags)0, Sdl.PInvoke.WasInit(SdlInitFlags.Video));

        using SdlVideoSession second = SdlVideoSession.Start(Options(HeadlessMode.NoSurface), null).Value;

        Assert.Equal(HeadlessMode.NoSurface, second.Headless);
    }

    [Fact]
    public void Start_AfterFailedStart_Succeeds()
    {
        using var variable = NativeEnvironmentVariable.Set(VideoDriverVariable, "dummy");
        Assert.True(SdlVideoSession.Start(Options(HeadlessMode.Offscreen), null).IsFailure);

        using SdlVideoSession session = SdlVideoSession.Start(Options(HeadlessMode.NoSurface), null).Value;

        Assert.Equal(HeadlessMode.NoSurface, session.Headless);
    }

    [Fact]
    public void Start_InvalidOptions_ThrowsBeforeStartingSdl()
    {
        using var variable = NativeEnvironmentVariable.Set(VideoDriverVariable, null);
        Result<SdlVideoSession> started = default;
        try
        {
            _ = Assert.Throws<ArgumentOutOfRangeException>(() => started = SdlVideoSession.Start(Options(HeadlessMode.NoSurface) with { EventCapacity = 0 }, null));
        }
        finally
        {
            // A regression that returns a session instead of throwing must not leave SDL started for later tests.
            if (started.TryGetValue(out SdlVideoSession? leaked))
            {
                leaked.Dispose();
            }
        }

        Assert.Equal((SdlInitFlags)0, Sdl.PInvoke.WasInit(SdlInitFlags.Video));

        using SdlVideoSession session = SdlVideoSession.Start(Options(HeadlessMode.NoSurface), null).Value;

        Assert.Equal(HeadlessMode.NoSurface, session.Headless);
    }

    [Fact]
    public void Start_DisposedLogger_ThrowsAndLeavesNoSessionLive()
    {
        using var variable = NativeEnvironmentVariable.Set(VideoDriverVariable, null);
        Logger logger = new(new LoggerOptions { Sinks = [new MemoryLogSink(1, 64)] });
        logger.Dispose();

        Result<SdlVideoSession> started = default;
        try
        {
            _ = Assert.Throws<ObjectDisposedException>(() => started = SdlVideoSession.Start(Options(HeadlessMode.NoSurface, logger), null));
        }
        finally
        {
            // A regression that returns a session instead of throwing must not leave SDL started for later tests.
            if (started.TryGetValue(out SdlVideoSession? leaked))
            {
                leaked.Dispose();
            }
        }

        Assert.Equal((SdlInitFlags)0, Sdl.PInvoke.WasInit(SdlInitFlags.Video));

        using SdlVideoSession session = SdlVideoSession.Start(Options(HeadlessMode.NoSurface), null).Value;

        Assert.Equal(HeadlessMode.NoSurface, session.Headless);
    }

    [Fact]
    public void Dispose_Twice_EndsTheSessionOnce()
    {
        using var variable = NativeEnvironmentVariable.Set(VideoDriverVariable, null);
        SdlVideoSession disposed = SdlVideoSession.Start(Options(HeadlessMode.NoSurface), null).Value;
        disposed.Dispose();
        using SdlVideoSession live = SdlVideoSession.Start(Options(HeadlessMode.NoSurface), null).Value;

        disposed.Dispose();

        Assert.Equal(SdlInitFlags.Video, Sdl.PInvoke.WasInit(SdlInitFlags.Video));
        _ = Assert.Throws<InvalidOperationException>(() => SdlVideoSession.Start(Options(HeadlessMode.NoSurface), null));
    }

    [Fact]
    public void Start_VulkanLibraryPath_SetsHint()
    {
        string path = Path.Combine(Path.GetTempPath(), "vulkan-loader-test");
        using var variable = NativeEnvironmentVariable.Set(VideoDriverVariable, null);
        using var vulkan = NativeEnvironmentVariable.Set("SDL_VULKAN_LIBRARY", null);

        using SdlVideoSession session = SdlVideoSession.Start(Options(HeadlessMode.NoSurface), new() { VulkanLibraryPath = path }).Value;

        Assert.Equal(path, Sdl.PInvoke.GetHint("SDL_VULKAN_LIBRARY"));
    }

    [Fact]
    public void Start_EnvironmentOverridesVulkanHint_LogsInformationAndStarts()
    {
        MemoryLogSink sink = new(16, 4096);
        using Logger logger = new(new LoggerOptions { Sinks = [sink] });
        using var variable = NativeEnvironmentVariable.Set(VideoDriverVariable, null);
        using var vulkan = NativeEnvironmentVariable.Set("SDL_VULKAN_LIBRARY", "from-environment");

        using (SdlVideoSession.Start(Options(HeadlessMode.NoSurface, logger), new() { VulkanLibraryPath = Path.GetTempPath() }).Value)
        {
            Assert.Equal("from-environment", Sdl.PInvoke.GetHint("SDL_VULKAN_LIBRARY"));
        }

        logger.Flush();
        var records = new MemoryLogRecord[16];
        byte[] messages = new byte[4096];
        int count = sink.CopyTo(records, messages);
        MemoryLogRecord record = Assert.Single(records.AsSpan(0, count).ToArray());
        Assert.Equal(LogLevel.Information, record.Level);
        Assert.Equal("Platform", record.Category);
        Assert.Contains("SDL_VULKAN_LIBRARY", Encoding.UTF8.GetString(record.Message.Span), StringComparison.Ordinal);
    }
}
