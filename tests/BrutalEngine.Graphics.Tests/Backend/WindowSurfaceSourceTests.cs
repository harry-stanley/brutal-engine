using System;
using BrutalEngine.Core;
using BrutalEngine.Graphics.Backend;
using BrutalEngine.Platform;
using Xunit;

namespace BrutalEngine.Graphics.Tests.Backend;

/// <summary>
/// The window surface handover a backend receives (docs/design/graphics.md section 3): forwarding to the window's
/// surface source, and the size snapshot <c>Refresh</c> takes. The project turns background garbage collection off,
/// which AllocationMeasurementTests guards, so the allocation count is exact.
/// </summary>
public sealed class WindowSurfaceSourceTests
{
    private static readonly byte[][] _extensions = ["VK_KHR_surface"u8.ToArray(), "VK_KHR_win32_surface"u8.ToArray()];

    [Fact]
    public void Forwards_ExtensionsAndCreate()
    {
        var source = new FakeVulkanSurfaceSource(_extensions, (nint)0x5eed);
        var surface = new WindowSurfaceSource(new FakeWindow { PixelSize = new(8, 8) }, source);

        Assert.Equal(2, surface.RequiredInstanceExtensionCount);
        Assert.True(surface.GetRequiredInstanceExtension(0).SequenceEqual("VK_KHR_surface"u8));
        Assert.True(surface.GetRequiredInstanceExtension(1).SequenceEqual("VK_KHR_win32_surface"u8));
        // The same storage, not a copy: the span points into the source's array.
        Assert.True(surface.GetRequiredInstanceExtension(1).Overlaps(_extensions[1]));

        Result<nint> created = surface.CreateVulkanSurface(0x1234);

        Assert.Equal((nint)0x5eed, created.Value);
        Assert.Equal((1, (nint)0x1234), (source.CreateCalls, source.LastInstance));
    }

    [Fact]
    public void Forwards_FailedSurfaceUnchanged()
    {
        var error = EngineError.Native(-7, "SDL_Vulkan_CreateSurface failed: no surface");
        var surface = new WindowSurfaceSource(new FakeWindow(), new FakeVulkanSurfaceSource(_extensions, error));

        Result<nint> created = surface.CreateVulkanSurface(0x1234);

        Assert.True(created.TryGetError(out EngineError actual));
        Assert.Equal(error, actual);
    }

    public static TheoryData<string> SourceExceptions() =>
        [nameof(ObjectDisposedException), nameof(ArgumentOutOfRangeException), nameof(ArgumentException), nameof(WrongThreadException)];

    [Theory]
    [MemberData(nameof(SourceExceptions))]
    public void Forwards_SourceExceptions(string type)
    {
        Exception failure = type switch
        {
            nameof(ObjectDisposedException) => new ObjectDisposedException("window"),
            nameof(ArgumentOutOfRangeException) => new ArgumentOutOfRangeException(paramName: null, "The extension index is out of range."),
            nameof(ArgumentException) => new ArgumentException("The instance is 0."),
            nameof(WrongThreadException) => new WrongThreadException("IVulkanSurfaceSource.CreateVulkanSurface", 1, 2),
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown exception."),
        };
        var source = new FakeVulkanSurfaceSource(_extensions, (nint)1);
        var surface = new WindowSurfaceSource(new FakeWindow(), source);
        source.Failure = failure;

        Assert.Same(failure, Assert.ThrowsAny<Exception>(() => surface.RequiredInstanceExtensionCount));
        Assert.Same(failure, Assert.ThrowsAny<Exception>(() => surface.GetRequiredInstanceExtension(0).Length));
        Assert.Same(failure, Assert.ThrowsAny<Exception>(() => surface.CreateVulkanSurface(1)));
    }

    [Fact]
    public void Constructor_TakesTheFirstSnapshot()
    {
        var window = new FakeWindow { PixelSize = new(1920, 1080), State = WindowState.Visible };

        var surface = new WindowSurfaceSource(window, new FakeVulkanSurfaceSource(_extensions, (nint)1));

        Assert.Equal((1920, 1080, false), (surface.PixelWidth, surface.PixelHeight, surface.IsMinimised));
    }

    public static TheoryData<int, int, WindowState, bool> RefreshCases() => new()
    {
        { 1280, 720, WindowState.Visible | WindowState.InputFocus, false },
        { 1280, 720, WindowState.None, false },
        { 1280, 720, WindowState.Visible | WindowState.Minimized, true },
        { 1280, 720, WindowState.Minimized, true },
        { 0, 720, WindowState.Visible, true },
        { 1280, 0, WindowState.Visible, true },
        { 0, 0, WindowState.Visible, true },
        { 0, 0, WindowState.Minimized, true },
        { 1, 1, WindowState.Maximized | WindowState.Occluded, false },
    };

    [Theory]
    [MemberData(nameof(RefreshCases))]
    public void Refresh_ZeroSideOrMinimised_IsMinimised(int width, int height, WindowState state, bool minimised)
    {
        // Created from a different, presentable snapshot, so each field must really be updated by Refresh.
        var window = new FakeWindow { PixelSize = new(640, 480), State = WindowState.Visible };
        var surface = new WindowSurfaceSource(window, new FakeVulkanSurfaceSource(_extensions, (nint)1));
        window.PixelSize = new(width, height);
        window.State = state;

        surface.Refresh();

        Assert.Equal((width, height, minimised), (surface.PixelWidth, surface.PixelHeight, surface.IsMinimised));
    }

    [Fact]
    public void Refresh_AfterMinimised_IsPresentableAgain()
    {
        var window = new FakeWindow { PixelSize = new(0, 0), State = WindowState.Minimized };
        var surface = new WindowSurfaceSource(window, new FakeVulkanSurfaceSource(_extensions, (nint)1));
        window.PixelSize = new(800, 600);
        window.State = WindowState.Visible;

        surface.Refresh();

        Assert.Equal((800, 600, false), (surface.PixelWidth, surface.PixelHeight, surface.IsMinimised));
    }

    [Fact]
    public void Refresh_DisposedWindow_ThrowsAndKeepsTheSnapshot()
    {
        var window = new FakeWindow { PixelSize = new(320, 200), State = WindowState.Visible };
        var surface = new WindowSurfaceSource(window, new FakeVulkanSurfaceSource(_extensions, (nint)1));
        window.Dispose();

        Assert.Throws<ObjectDisposedException>(surface.Refresh);
        Assert.Equal((320, 200, false), (surface.PixelWidth, surface.PixelHeight, surface.IsMinimised));
    }

    [Fact]
    public void Constructor_NullArguments_Throw()
    {
        var source = new FakeVulkanSurfaceSource(_extensions, (nint)1);

        Assert.Equal("window", Assert.Throws<ArgumentNullException>(() => new WindowSurfaceSource(null!, source)).ParamName);
        Assert.Equal("source", Assert.Throws<ArgumentNullException>(() => new WindowSurfaceSource(new FakeWindow(), null!)).ParamName);
    }

    [Fact]
    public void RefreshAndGetters_DoNotAllocate()
    {
        const int Iterations = 1_000;
        var window = new FakeWindow { PixelSize = new(1920, 1080), State = WindowState.Visible };
        var surface = new WindowSurfaceSource(window, new FakeVulkanSurfaceSource(_extensions, (nint)1));
        long checksum = Exercise(surface, window, 0);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 1; i <= Iterations; i++)
        {
            checksum += Exercise(surface, window, i);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
        Assert.NotEqual(0, checksum);
    }

    // Alternates between a minimised and a presentable snapshot, so Refresh takes both paths, and reads every getter.
    private static long Exercise(WindowSurfaceSource surface, FakeWindow window, int i)
    {
        window.PixelSize = new(1920 + (i & 1), (i & 2) == 0 ? 1080 : 0);
        window.State = (i & 4) == 0 ? WindowState.Visible : WindowState.Minimized;
        surface.Refresh();
        return surface.PixelWidth + surface.PixelHeight + (surface.IsMinimised ? 1 : 0)
            + surface.RequiredInstanceExtensionCount + surface.GetRequiredInstanceExtension(1).Length;
    }
}
