using System;
using BrutalEngine.Core;
using BrutalEngine.Platform;

namespace BrutalEngine.TestUtilities.Tests;

/// <summary>
/// A window for building <c>WindowSurfaceSource</c> instances: <see cref="PixelSize"/> and <see cref="State"/> are settable
/// snapshots, and every other member throws, because a surface source reads nothing else.
/// </summary>
internal sealed class TestWindow : IWindow
{
    public WindowSize PixelSize { get; set; }

    public WindowState State { get; set; }

    public IVulkanSurfaceSource? VulkanSurface => throw Unused();

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

    public void Dispose()
    {
    }

    private static NotSupportedException Unused() => new("A surface source doesn't use this window member.");
}
