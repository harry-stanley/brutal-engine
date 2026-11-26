using System;
using BrutalEngine.Core;
using BrutalEngine.Platform;

namespace BrutalEngine.Graphics.Tests.Backend;

/// <summary>
/// A test window: <see cref="PixelSize"/>, <see cref="State"/> and <see cref="VulkanSurface"/> are settable snapshots,
/// and every other member throws, so a test fails if the code under test reads anything else. After
/// <see cref="Dispose"/>, the two snapshots throw <see cref="ObjectDisposedException"/> as a real window does.
/// <see cref="StateFailure"/> makes <see cref="State"/> alone throw, so a test can fail the second read of a refresh.
/// </summary>
internal sealed class FakeWindow : IWindow
{
    private WindowSize _pixelSize;
    private WindowState _state;

    public bool IsDisposed { get; private set; }

    public Exception? StateFailure { get; set; }

    public WindowSize PixelSize
    {
        get => IsDisposed ? throw new ObjectDisposedException(nameof(FakeWindow)) : _pixelSize;
        set => _pixelSize = value;
    }

    public WindowState State
    {
        get => IsDisposed ? throw new ObjectDisposedException(nameof(FakeWindow)) : StateFailure is null ? _state : throw StateFailure;
        set => _state = value;
    }

    public IVulkanSurfaceSource? VulkanSurface { get; set; }

    public WindowId Id => throw Unused();

    public WindowSize Size => throw Unused();

    public WindowSize ContentSize => throw Unused();

    public float PixelDensity => throw Unused();

    public float DisplayScale => throw Unused();

    public WindowMode Mode => throw Unused();

    public DisplayId Display => throw Unused();

    public bool IsTextInputActive => throw Unused();

    public bool IsRelativeMouseMode => throw Unused();

    public Result SetTitle(string title) => throw Unused();

    public Result SetContentSize(WindowSize contentSize) => throw Unused();

    public Result SetMode(WindowMode mode) => throw Unused();

    public Result SetExclusiveFullscreen(DisplayMode mode) => throw Unused();

    public Result StartTextInput() => throw Unused();

    public Result StopTextInput() => throw Unused();

    public Result SetRelativeMouseMode(bool enabled) => throw Unused();

    public void Dispose() => IsDisposed = true;

    private static NotSupportedException Unused() => new("WindowSurfaceSource must not use this window member.");
}
