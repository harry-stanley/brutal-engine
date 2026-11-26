using System;
using System.Threading;
using Brutal.SdlApi;
using Xunit;

namespace BrutalEngine.Platform.Sdl3.Tests;

/// <summary>
/// Test scaffolding for the Gpu tests: SDL video and one SDL Vulkan window with its surface source. Windowed on
/// Windows and on SDL's <c>offscreen</c> driver elsewhere, as the vulkan-smoke job runs (docs/design/platform.md
/// section 12). Dispose destroys the window, then stops SDL.
/// </summary>
internal sealed class VulkanWindowHarness : IDisposable, SdlVulkanSurfaceSource.IOwner
{
    private readonly NativeEnvironmentVariable _videoDriver;
    private readonly SdlVideoSession? _session;
    private SdlWindow _window;
    private int _disposed;

    public VulkanWindowHarness()
    {
        _videoDriver = NativeEnvironmentVariable.Set("SDL_VIDEO_DRIVER", null);
        try
        {
            HeadlessMode headless = OperatingSystem.IsWindows() ? HeadlessMode.None : HeadlessMode.Offscreen;
            PlatformOptions options = new() { Organization = "BrutalEngine", Application = "Platform.Sdl3.Tests", Headless = headless };
            _session = SdlVideoSession.Start(options, null).Value;
            _window = Sdl.PInvoke.CreateWindow("Platform.Sdl3.Tests", 64, 64, SdlWindowFlags.Vulkan | SdlWindowFlags.Hidden);
            Assert.False(_window.IsNull(), Sdl.PInvoke.GetError());
            Source = SdlVulkanSurfaceSource.Create(_window, this).Value;
        }
        catch
        {
            // A failed setup must not leave SDL started: every later test in the collection would fail behind it.
            Dispose();
            throw;
        }
    }

    public SdlVulkanSurfaceSource Source { get; } = null!;

    /// <summary>What the source sees as its window's disposed flag; tests set it to check the disposed paths.</summary>
    public bool IsDisposed
    {
        get => Volatile.Read(ref _disposed) != 0;
        set => Volatile.Write(ref _disposed, value ? 1 : 0);
    }

    public void Dispose()
    {
        if (!_window.IsNull())
        {
            Sdl.PInvoke.DestroyWindow(_window);
            _window = default;
        }

        _session?.Dispose();
        _videoDriver.Dispose();
    }
}
