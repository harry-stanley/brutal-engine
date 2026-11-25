using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Brutal.SdlApi;
using Brutal.VulkanApi;

namespace HelloWindow;

/// <summary>Runs the sample: SDL and Vulkan setup, the event and frame loop, and teardown in reverse order.</summary>
internal sealed unsafe class HelloWindowApp
{
    private readonly HelloWindowOptions _options;
    private bool _setupComplete;
    private int _resizes;

    private HelloWindowApp(HelloWindowOptions options) => _options = options;

    /// <summary>Runs the sample and maps the outcome to an exit code.</summary>
    /// <param name="options">The parsed command line.</param>
    /// <returns><see cref="ExitCode.Pass"/>, or why the run failed.</returns>
    public static ExitCode Run(HelloWindowOptions options) => new HelloWindowApp(options).RunAndReport();

    private ExitCode RunAndReport()
    {
        ExitCode result = ExitCode.Pass;
        try
        {
            RunWithSdl();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            result = _setupComplete ? ExitCode.RuntimeFailure : ExitCode.SetupFailure;
            SampleConsole.Error($"{(_setupComplete ? "Runtime failure" : "Setup failure")}: {exception.Message}");
            SampleConsole.Error(exception.ToString());
        }

        if (_options.Validation)
        {
            SampleConsole.Info($"Validation: {ValidationMessenger.Errors} error(s), {ValidationMessenger.Warnings} warning(s).");
            if (result == ExitCode.Pass && ValidationMessenger.Errors + ValidationMessenger.Warnings > 0)
            {
                result = ExitCode.ValidationMessages;
            }
        }

        SampleConsole.Info(result == ExitCode.Pass ? "PASS" : $"FAIL ({result})");
        return result;
    }

    private void RunWithSdl()
    {
        // BRUTAL resolves the Vulkan loader once, on first use of its Vulkan class. Do that before SDL loads Vulkan for
        // the window, so both use the same library.
        if (_options.VulkanLoader is { } loader)
        {
            PreloadVulkanLoader(loader);
        }
        else
        {
            _ = Vulkan.GetInstanceProcAddr;
        }

        if (_options.Headless)
        {
            // SDL 3.4's offscreen driver creates Vulkan surfaces with VK_EXT_headless_surface.
            NativeCheck.Sdl(Sdl.PInvoke.SetHint(Sdl.Constants.HintVideoDriver, "offscreen") != 0, "SDL_SetHint(SDL_VIDEO_DRIVER)");
        }

        Sdl.Init.Initialize(SdlInitFlags.Video);
        try
        {
            SampleConsole.Info($"SDL video driver: {Sdl.Video.GetCurrentVideoDriver()}");
            using SdlWindow window = Sdl.Video.CreateWindow(
                "BrutalEngine HelloWindow",
                _options.Width,
                _options.Height,
                SdlWindowFlags.Vulkan | SdlWindowFlags.Resizable | SdlWindowFlags.HighPixelDensity);
            using var vulkan = VulkanContext.Create(window, _options);
            using var renderer = new FrameRenderer(vulkan);
            RunFrames(window, vulkan, renderer);
        }
        finally
        {
            Sdl.Init.Quit();
        }
    }

    private void RunFrames(SdlWindow window, VulkanContext vulkan, FrameRenderer renderer)
    {
        Swapchain? swapchain = null;
        int presented = 0, recreations = 0, outOfDate = 0, suboptimal = 0;
        long frameBytes = 0;
        bool checkExtent = true, recreate = false;
        var clock = Stopwatch.StartNew();
        try
        {
            while (_options.Frames is not { } frames || presented < frames)
            {
                if (!PumpEvents(ref checkExtent))
                {
                    SampleConsole.Info($"Quit requested after {presented} frames.");
                    break;
                }

                ExerciseResize(window, presented);
                if (checkExtent || recreate || swapchain is null)
                {
                    VkSurfaceCapabilitiesKHR capabilities = vulkan.PhysicalDevice.GetSurfaceCapabilitiesKHR(vulkan.Surface);
                    NativeCheck.Sdl(Sdl.Video.GetWindowSizeInPixels(window, out int width, out int height), "SDL_GetWindowSizeInPixels");
                    SwapchainExtent extent = SwapchainPlanner.ChooseExtent(capabilities, width, height);
                    if (extent.IsMinimised || (Sdl.Video.GetWindowFlags(window) & SdlWindowFlags.Minimized) != 0)
                    {
                        // Nothing to render to: sleep in SDL until the next event (restore, resize or quit).
                        if (!WaitForEvent())
                        {
                            break;
                        }

                        checkExtent = true;
                        continue;
                    }

                    // SDL reports a resize right after creating the window, so recreate only if the size really changed.
                    if (recreate || swapchain is null || extent != swapchain.Extent)
                    {
                        NativeCheck.Vk(vulkan.WaitIdle(), "vkDeviceWaitIdle");
                        Swapchain? old = swapchain;
                        swapchain = null;
                        try
                        {
                            swapchain = Swapchain.Create(vulkan, extent, capabilities, old);
                        }
                        finally
                        {
                            // Retired by the create call even if it failed, so it is always destroyed.
                            old?.Destroy();
                        }

                        recreations += old is null ? 0 : 1;
                        _setupComplete = true;
                        SampleConsole.Info($"Swapchain {extent} {swapchain.Format.Format}/{swapchain.Format.ColorSpace}, {swapchain.Images.Length} images, {SwapchainPlanner.PresentMode} (at frame {presented})");
                    }

                    checkExtent = recreate = false;
                }

                // Frames must not allocate managed memory (docs/07-coding-standards.md); the first one may, while it warms up.
                long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                FrameAction action = renderer.RenderFrame(swapchain, _options.InjectValidationError && presented == 0);
                frameBytes += presented == 0 ? 0 : GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
                presented += action == FrameAction.Recreate ? 0 : 1;
                outOfDate += action == FrameAction.Recreate ? 1 : 0;
                suboptimal += action == FrameAction.RenderThenRecreate ? 1 : 0;
                recreate = action != FrameAction.Render;
            }

            NativeCheck.Vk(vulkan.WaitIdle(), "vkDeviceWaitIdle");
        }
        finally
        {
            // Destroying is valid even after a device loss, so teardown continues whatever the wait returns.
            VkResult idle = vulkan.WaitIdle();
            if (idle != VkResult.Success)
            {
                SampleConsole.Error($"vkDeviceWaitIdle returned {idle} during teardown.");
            }

            swapchain?.Destroy();
        }

        double seconds = clock.Elapsed.TotalSeconds;
        SampleConsole.Info($"Presented {presented} frames in {seconds:F2} s ({presented / seconds:F1} fps); "
            + $"swapchain recreations {recreations}, out-of-date {outOfDate}, suboptimal {suboptimal}; "
            + $"{frameBytes} managed bytes allocated by frames after the first.");
        if (_options.ExerciseResize && recreations == 0)
        {
            throw new InvalidOperationException("--exercise-resize: the swapchain was never recreated.");
        }
    }

    private void ExerciseResize(SdlWindow window, int presented)
    {
        // Shrink at a third of the run and restore at two thirds: two size changes, each needing a new swapchain.
        if (!_options.ExerciseResize || _options.Frames is not { } frames)
        {
            return;
        }

        if ((_resizes == 0 && presented >= frames / 3) || (_resizes == 1 && presented >= 2 * frames / 3))
        {
            bool shrink = _resizes++ == 0;
            int width = shrink ? Math.Max(1, _options.Width * 3 / 4) : _options.Width;
            int height = shrink ? Math.Max(1, _options.Height * 3 / 4) : _options.Height;
            SampleConsole.Info($"Resizing the window to {width}x{height} (at frame {presented})");
            NativeCheck.Sdl(Sdl.Video.SetWindowSize(window, width, height), "SDL_SetWindowSize");
        }
    }

    private static bool PumpEvents(ref bool checkExtent)
    {
        while (Sdl.Events.PollEvent(out SdlEvent sdlEvent))
        {
            if (!Handle(sdlEvent, ref checkExtent))
            {
                return false;
            }
        }

        return true;
    }

    private static bool WaitForEvent()
    {
        SdlEvent sdlEvent = default;
        NativeCheck.Sdl(Sdl.Events.WaitEvent(&sdlEvent), "SDL_WaitEvent");
        bool unused = true;
        return Handle(sdlEvent, ref unused) && PumpEvents(ref unused);
    }

    // Returns false when the user asked to quit.
    private static bool Handle(in SdlEvent sdlEvent, ref bool checkExtent)
    {
        switch (sdlEvent.type)
        {
            case SdlEventType.Quit or SdlEventType.WindowCloseRequested:
                return false;
            case SdlEventType.KeyDown when sdlEvent.key.key == SdlKeycode.Escape:
                return false;
            case SdlEventType.WindowPixelSizeChanged or SdlEventType.WindowResized or SdlEventType.WindowMinimized or SdlEventType.WindowRestored:
                checkExtent = true;
                return true;
            default:
                return true;
        }
    }

    private static void PreloadVulkanLoader(string path)
    {
        // BRUTAL looks the loader up by bare name, which binds to a library of that name already in the process
        // (spike S1, docs/spikes/README.md). Check it really did, rather than silently using another loader.
        nint library = NativeLibrary.Load(path);
        nint entryPoint = NativeLibrary.GetExport(library, "vkGetInstanceProcAddr");
        if ((nint)Vulkan.GetInstanceProcAddr.Invoke != entryPoint)
        {
            throw new InvalidOperationException($"BRUTAL resolved a different Vulkan loader than {path}.");
        }

        NativeCheck.Sdl(Sdl.PInvoke.SetHint(Sdl.Constants.HintVulkanLibrary, path) != 0, "SDL_SetHint(SDL_VULKAN_LIBRARY)");
        SampleConsole.Info($"Vulkan loader: {path} (preloaded, used by BRUTAL and SDL)");
    }
}
