using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Brutal.SdlApi;
using BrutalEngine.Core;
using BrutalEngine.Graphics.Vulkan.Instances;
using BrutalEngine.Platform;
using Xunit;

namespace BrutalEngine.Graphics.Vulkan.Tests.Adapters;

/// <summary>
/// A hidden 64x64 SDL window with a Vulkan surface, over BRUTAL's SDL binding (test-only), playing both the platform
/// window and its surface source, so a test can hand it to <c>GraphicsDevice</c> as the compatible window. Windowed on
/// Windows; on SDL's <c>offscreen</c> driver (<c>VK_EXT_headless_surface</c>) elsewhere, as the vulkan-smoke job runs.
/// Members a surface source doesn't need throw.
/// </summary>
internal sealed unsafe class SdlTestWindow : IWindow, IVulkanSurfaceSource
{
    private const string VideoDriverHint = "SDL_VIDEO_DRIVER";
    private readonly byte[][] _extensions;
    private SdlWindow _window;

    public SdlTestWindow()
    {
        if (!OperatingSystem.IsWindows())
        {
            _ = Sdl.PInvoke.SetHint(VideoDriverHint, "offscreen");
        }

        // SDL's Vulkan setup makes global commands itself (the offscreen driver's extension list calls
        // vkEnumerateInstanceExtensionProperties), which crash when they overlap another test's (graphics-vulkan.md
        // section 1), so it runs inside the backend's gate.
        using Lock.Scope scope = GlobalCommands.Gate.EnterScope();
        try
        {
            Assert.True(Sdl.PInvoke.InitSubSystem(SdlInitFlags.Video) != 0, Sdl.PInvoke.GetError());
            _window = Sdl.PInvoke.CreateWindow(nameof(SdlTestWindow), 64, 64, SdlWindowFlags.Vulkan | SdlWindowFlags.Hidden);
            Assert.False(_window.IsNull(), Sdl.PInvoke.GetError());
            uint count;

            // Sound: count is a local; SDL owns the returned array of NUL-terminated names, valid while its Vulkan
            // library is loaded, and they are copied before returning.
            byte** names = Sdl.PInvoke.Vulkan_GetInstanceExtensions(&count);
            Assert.True(names is not null, Sdl.PInvoke.GetError());
            _extensions = new byte[count][];
            ExtensionNames = new string[count];
            for (int i = 0; i < count; i++)
            {
                _extensions[i] = MemoryMarshal.CreateReadOnlySpanFromNullTerminated(names[i]).ToArray();
                ExtensionNames[i] = Encoding.UTF8.GetString(_extensions[i]);
            }
        }
        catch
        {
            // A failed setup must not leave SDL's video started for the next test.
            Dispose();
            throw;
        }
    }

    public string[] ExtensionNames { get; }

    public int SurfacesCreated { get; private set; }

    public int RequiredInstanceExtensionCount => _extensions.Length;

    public WindowSize PixelSize => new(64, 64);

    public WindowState State => WindowState.None;

    public IVulkanSurfaceSource VulkanSurface => this;

    public WindowId Id => throw Unused();

    public WindowSize Size => throw Unused();

    public WindowSize ContentSize => throw Unused();

    public float PixelDensity => throw Unused();

    public float DisplayScale => throw Unused();

    public WindowMode Mode => throw Unused();

    public DisplayId Display => throw Unused();

    public bool IsTextInputActive => throw Unused();

    public bool IsRelativeMouseMode => throw Unused();

    public ReadOnlySpan<byte> GetRequiredInstanceExtension(int index) => _extensions[index];

    public Result<nint> CreateVulkanSurface(nint instance)
    {
        nint surface = 0;

        // Sound: the window is alive and surface is a local that SDL writes before returning; no allocation callbacks.
        if (Sdl.PInvoke.Vulkan_CreateSurface(_window, instance, 0, &surface) == 0)
        {
            return new EngineError(ErrorKind.Native, "SDL_Vulkan_CreateSurface failed: " + Sdl.PInvoke.GetError());
        }

        SurfacesCreated++;
        return surface;
    }

    public Result SetTitle(string title) => throw Unused();

    public Result SetContentSize(WindowSize contentSize) => throw Unused();

    public Result SetMode(WindowMode mode) => throw Unused();

    public Result SetExclusiveFullscreen(DisplayMode mode) => throw Unused();

    public Result StartTextInput() => throw Unused();

    public Result StopTextInput() => throw Unused();

    public Result SetRelativeMouseMode(bool enabled) => throw Unused();

    public void Dispose()
    {
        using Lock.Scope scope = GlobalCommands.Gate.EnterScope();
        if (!_window.IsNull())
        {
            Sdl.PInvoke.DestroyWindow(_window);
            _window = default;
        }

        Sdl.PInvoke.QuitSubSystem(SdlInitFlags.Video);
        _ = Sdl.PInvoke.ResetHint(VideoDriverHint);
    }

    private static NotSupportedException Unused() => new("The adapter tests' window has no such member.");
}
