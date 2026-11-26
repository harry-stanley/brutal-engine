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

    /// <summary>
    /// Asks every physical device for the surface's capabilities, as a backend choosing a device would: a host with more
    /// than one driver can list a device that can't use the surface (the NVIDIA driver lacks VK_EXT_headless_surface)
    /// before one that can (lavapipe), so the first device alone doesn't decide. Returns Success with the capabilities
    /// of the first device that accepts the surface; when none does, the first device's result with zeroed capabilities.
    /// Fails the test when the instance has no physical device.
    /// </summary>
    public VkResult QueryCapabilities(nint surface, out VkSurfaceCapabilitiesKHR capabilities)
    {
        int count = 0;

        // Sound: count is a local of this frame, which outlives the call, and the null array asks only for the count.
        VkResult counted = _table.vkEnumeratePhysicalDevices.Invoke(_instance, &count, null);
        Assert.Equal(VkResult.Success, counted);
        Assert.True(count > 0, "The Vulkan instance has no physical device to query the surface with.");

        var devices = new VkPhysicalDevice[count];
        VkResult enumerated;

        // Sound: devices is pinned for the call and count, a local, holds its length, so the driver writes at most
        // devices.Length handles and sets count to the number it wrote.
        fixed (VkPhysicalDevice* pinned = devices)
        {
            enumerated = _table.vkEnumeratePhysicalDevices.Invoke(_instance, &count, pinned);
        }

        // Incomplete means a device appeared between the two calls; the array still holds count valid handles.
        Assert.True(enumerated is VkResult.Success or VkResult.Incomplete, enumerated.ToString());
        Assert.InRange(count, 1, devices.Length);

        VkResult firstResult = VkResult.Success;
        for (int i = 0; i < count; i++)
        {
            VkSurfaceCapabilitiesKHR queried;

            // Sound: queried is a local of this frame, which outlives the call, and the device handle came from the
            // instance that owns the surface.
            VkResult result = _table.vkGetPhysicalDeviceSurfaceCapabilitiesKHR.Invoke(devices[i], new VkSurfaceKHR(surface), &queried);
            if (result == VkResult.Success)
            {
                capabilities = queried;
                return result;
            }

            if (i == 0)
            {
                firstResult = result;
            }
        }

        capabilities = default;
        return firstResult;
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
