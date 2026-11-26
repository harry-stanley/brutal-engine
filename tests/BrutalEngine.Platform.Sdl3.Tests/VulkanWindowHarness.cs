using System;
using System.Threading;
using Brutal.SdlApi;
using Brutal.VulkanApi;
using BrutalEngine.Core;
using Xunit;

namespace BrutalEngine.Platform.Sdl3.Tests;

/// <summary>
/// Test scaffolding for the Gpu tests: SDL video, one SDL Vulkan window with its surface source, and a minimal
/// VkInstance created through BRUTAL's raw global command (<c>Vulkan.PInvoke.vkCreateInstance</c>,
/// Brutal.Vulkan/Generated/PInvoke/VulkanApi/VulkanApi.gen.cs:26) and used through its raw instance table
/// (<c>VkInstance.FunctionTable</c>, Brutal.Vulkan/Generated/Handles/VkInstance.gen.cs:152). Windowed on Windows and
/// on SDL's <c>offscreen</c> driver elsewhere, as the vulkan-smoke job runs (docs/design/platform.md section 12).
/// Dispose destroys the instance, then the window, then stops SDL; a test destroys its surface before that.
/// </summary>
internal sealed unsafe class VulkanWindowHarness : IDisposable, SdlVulkanSurfaceSource.IOwner
{
    private readonly NativeEnvironmentVariable _videoDriver;
    private readonly SdlVideoSession? _session;
    private SdlWindow _window;
    private VkInstance _instance;
    private VkInstance.FunctionTable _table;
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
            Source = SdlVulkanSurfaceSource.Create(_window, ThreadAffinity.CurrentThread(), this).Value;
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

    /// <summary>Creates the VkInstance, with SDL's required instance extensions or with none.</summary>
    public nint CreateInstance(bool withRequiredExtensions = true)
    {
        Assert.True(_instance.IsNull(), "The harness creates one instance.");

        // Reading GetInstanceProcAddr runs Vulkan's static constructor, which loads the loader and registers the
        // resolver for the "vulkan" imports (Brutal.Vulkan/ImportResolver.cs:16-37); Vulkan.PInvoke doesn't run it.
        VkGetInstanceProcAddr getProc = Vulkan.GetInstanceProcAddr;
        int count = withRequiredExtensions ? Source.RequiredInstanceExtensionCount : 0;
        int[] starts = new int[count];
        byte[] text = new byte[256 * (count + 1)]; // VK_MAX_EXTENSION_NAME_SIZE bytes per name, its NUL included
        for (int i = 0, end = 0; i < count; i++)
        {
            ReadOnlySpan<byte> name = Source.GetRequiredInstanceExtension(i);
            starts[i] = end;
            name.CopyTo(text.AsSpan(end));
            end += name.Length + 1; // the zeroed byte after each name is its NUL terminator
        }

        nint* names = stackalloc nint[count + 1];

        // Sound: text is pinned and names is on the stack, and both outlive vkCreateInstance.
        fixed (byte* pinned = text)
        {
            for (int i = 0; i < count; i++)
            {
                names[i] = (nint)(pinned + starts[i]);
            }

            VkInstanceCreateInfo info = new() { EnabledExtensionCount = count, ppEnabledExtensionNames = (byte**)names };
            VkInstance instance;
            Assert.Equal(VkResult.Success, Vulkan.PInvoke.vkCreateInstance(&info, null, &instance));
            _instance = instance;
        }

        _table = new VkInstance.FunctionTable(_instance, getProc);
        return _instance.VkHandle;
    }

    /// <summary>Calls vkGetPhysicalDeviceSurfaceCapabilitiesKHR on the first physical device: a surface it accepts is usable.</summary>
    public VkResult QueryCapabilities(nint surface)
    {
        // Sound: count, device and capabilities are locals of this frame, which outlives both calls, and count is 1, so
        // the driver writes at most one handle into device; device is read only after Assert.Equal(1, count) proves the
        // driver wrote it.
        int count = 1;
        VkPhysicalDevice device;
        VkResult enumerated = _table.vkEnumeratePhysicalDevices.Invoke(_instance, &count, &device);
        Assert.True(enumerated is VkResult.Success or VkResult.Incomplete, enumerated.ToString());
        Assert.Equal(1, count);
        VkSurfaceCapabilitiesKHR capabilities;
        return _table.vkGetPhysicalDeviceSurfaceCapabilitiesKHR.Invoke(device, new VkSurfaceKHR(surface), &capabilities);
    }

    /// <summary>Destroys a surface the source created, with vkDestroySurfaceKHR from the instance, as a backend would.</summary>
    // Sound: the only pointer is the null allocator, which Vulkan accepts; tests destroy their surface before Dispose
    // destroys the instance.
    public void DestroySurface(nint surface) => _table.vkDestroySurfaceKHR.Invoke(_instance, new VkSurfaceKHR(surface), null);

    public void Dispose()
    {
        if (!_instance.IsNull())
        {
            // Sound: the only pointer is the null allocator, which Vulkan accepts, and the instance is live.
            _table.vkDestroyInstance.Invoke(_instance, null);
            _instance = default;
        }

        if (!_window.IsNull())
        {
            Sdl.PInvoke.DestroyWindow(_window);
            _window = default;
        }

        _session?.Dispose();
        _videoDriver.Dispose();
    }
}
