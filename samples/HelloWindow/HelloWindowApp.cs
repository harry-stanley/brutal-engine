using System;
using System.Diagnostics;
using System.IO;
using Brutal.SdlApi;
using Brutal.VulkanApi;
using BrutalEngine.Core.Logging;
using HelloWindow.Splash;

namespace HelloWindow;

/// <summary>Runs the sample: SDL and Vulkan setup, the event and frame loop (the BRUTAL splash, then the scene), and
/// teardown in reverse order.</summary>
internal sealed unsafe class HelloWindowApp
{
    // The least space between the splash logo and the window's edges, in pixels.
    private const int SplashMargin = 32;

    private readonly HelloWindowOptions _options;
    private readonly LogCategory _log;
    private readonly LogCategory _vulkanLog;
    private bool _setupComplete;
    private int _resizes;

    private HelloWindowApp(HelloWindowOptions options, SampleLog log)
    {
        _options = options;
        _log = log.App;
        _vulkanLog = log.Vulkan;
    }

    /// <summary>Runs the sample and maps the outcome to an exit code.</summary>
    /// <param name="options">The parsed command line.</param>
    /// <param name="log">The sample's log.</param>
    /// <returns><see cref="ExitCode.Pass"/>, or why the run failed.</returns>
    public static ExitCode Run(HelloWindowOptions options, SampleLog log) => new HelloWindowApp(options, log).RunAndReport();

    private ExitCode RunAndReport()
    {
        // Disposed last: the instance calls the messenger until vkDestroyInstance, inside RunWithSdl, has returned.
        using ValidationMessenger? validation = _options.Validation ? new ValidationMessenger(_vulkanLog) : null;
        ExitCode result = ExitCode.Pass;
        try
        {
            RunWithSdl(validation);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            result = _setupComplete ? ExitCode.RuntimeFailure : ExitCode.SetupFailure;
            _log.Error($"{(_setupComplete ? "Runtime failure" : "Setup failure")}: {exception.Message}");

            // Typed failures name the failing call or requirement in their message; only unexpected ones need the trace.
            if (FailureReport.NeedsStackTrace(exception))
            {
                _log.Error($"{exception}");
            }
        }

        if (validation is not null)
        {
            int messages = validation.Errors + validation.Warnings;
            _log.Log(messages == 0 ? LogLevel.Information : LogLevel.Error, $"Validation: {validation.Errors} error(s), {validation.Warnings} warning(s).");
            if (result == ExitCode.Pass && messages > 0)
            {
                result = ExitCode.ValidationMessages;
            }
        }

        if (result == ExitCode.Pass)
        {
            _log.Information($"PASS");
        }
        else
        {
            _log.Error($"FAIL ({result})");
        }
        return result;
    }

    private void RunWithSdl(ValidationMessenger? validation)
    {
        // BRUTAL resolves the Vulkan loader once, on first use of its Vulkan class. Do that before SDL loads Vulkan for
        // the window, so both use the same library. A published macOS build brings its own loader and MoltenVK; an
        // explicit --vulkan-loader wins over the bundled loader.
        if (BundledVulkan.TryConfigure(AppContext.BaseDirectory, _options.VulkanLoader, new BundledVulkan.ProcessHost(_vulkanLog)))
        {
            _vulkanLog.Information($"Using the Vulkan natives bundled in {AppContext.BaseDirectory}");
        }

        if (_options.VulkanLoader is { } loader)
        {
            BundledVulkan.PreloadLoader(loader, "--vulkan-loader", _vulkanLog);
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
            _log.Information($"SDL video driver: {Sdl.Video.GetCurrentVideoDriver()}");

            // BRUTAL LICENSE 2.2: the logo comes before the scene. A missing or unreadable logo fails setup.
            string logoPath = Path.Join(AppContext.BaseDirectory, SplashLogo.AssetsFolder, SplashLogo.FileName);
            var logo = SplashLogo.Load(logoPath);
            _log.Information($"Splash logo: {logoPath} ({logo.Size})");
            using SdlWindow window = Sdl.Video.CreateWindow(
                "BrutalEngine HelloWindow",
                _options.Width,
                _options.Height,
                SdlWindowFlags.Vulkan | SdlWindowFlags.Resizable | SdlWindowFlags.HighPixelDensity);
            using var vulkan = VulkanContext.Create(window, _options, validation, _vulkanLog);
            using var renderer = new FrameRenderer(vulkan);
            RunFrames(window, vulkan, renderer, logo);
        }
        finally
        {
            Sdl.Init.Quit();
        }
    }

    private void RunFrames(SdlWindow window, VulkanContext vulkan, FrameRenderer renderer, SplashLogo logo)
    {
        // Headless runs count splash frames; windowed runs time the splash (SplashTimeline).
        SplashTimeline timeline = _options.Headless ? SplashTimeline.Headless() : SplashTimeline.Windowed(Stopwatch.Frequency);
        var splashStatistics = new SplashStatistics();
        SplashImage? splashImage = null;
        SplashDraw splash = default;
        bool splashMinimumSize = false;
        Swapchain? swapchain = null;
        int presented = 0, recreations = 0, outOfDate = 0, suboptimal = 0, steadyFrames = 0;
        long steadyBytes = 0;
        bool checkExtent = true, recreate = false;
        var clock = Stopwatch.StartNew();
        try
        {
            while (_options.Frames is not { } frames || presented < frames)
            {
                // Steady-state frames must not allocate managed memory (docs/07-coding-standards.md). Measure the whole
                // iteration, except the first frame (warm-up) and iterations that resize or recreate the swapchain.
                long iterationStart = GC.GetAllocatedBytesForCurrentThread();
                bool steadyState = presented > 0;
                if (!PumpEvents(ref checkExtent))
                {
                    _log.Information($"Quit requested after {presented} frames.");
                    break;
                }

                steadyState &= !ExerciseResize(window, presented);
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
                        steadyState = false;
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
                        _vulkanLog.Information($"Swapchain {extent} {swapchain.Format.Format}/{swapchain.Format.ColorSpace}, {swapchain.Images.Length} images, {swapchain.Usage}, {SwapchainPlanner.PresentMode} (at frame {presented})");

                        // For the first swapchain this runs before setup counts as complete, so a surface that can't show
                        // the splash is a setup failure (exit 3). Later swapchains keep the format and its features, and
                        // on the copy path the window can't shrink below the logo while the splash lasts.
                        if (!timeline.IsComplete)
                        {
                            splash = PrepareSplash(window, vulkan, swapchain, logo, ref splashImage, ref splashMinimumSize);
                        }
                        else if (splashImage is not null)
                        {
                            // The splash is over and the device is idle: the logo image is no longer needed.
                            splashImage.Dispose();
                            splashImage = null;
                            splash = default;
                        }

                        _setupComplete = true;
                    }

                    checkExtent = recreate = false;
                }

                long decided = Stopwatch.GetTimestamp();
                bool showSplash = timeline.ShowsSplash(presented, decided);
                if (!showSplash && splashMinimumSize)
                {
                    // The splash is over: the window may shrink freely again.
                    steadyState = false;
                    splashMinimumSize = false;
                    NativeCheck.Sdl(Sdl.Video.SetWindowMinimumSize(window, 0, 0), "SDL_SetWindowMinimumSize");
                    _log.Information($"Splash over: window minimum size cleared (at frame {presented})");
                }

                FrameAction action = renderer.RenderFrame(swapchain, showSplash ? splash : null, _options.InjectValidationError && presented == 0);
                if (action != FrameAction.Recreate)
                {
                    splashStatistics.Presented(showSplash, decided);
                }

                presented += action == FrameAction.Recreate ? 0 : 1;
                outOfDate += action == FrameAction.Recreate ? 1 : 0;
                suboptimal += action == FrameAction.RenderThenRecreate ? 1 : 0;
                recreate = action != FrameAction.Render;
                if (steadyState)
                {
                    steadyBytes += GC.GetAllocatedBytesForCurrentThread() - iterationStart;
                    steadyFrames++;
                }
            }

            NativeCheck.Vk(vulkan.WaitIdle(), "vkDeviceWaitIdle");
        }
        finally
        {
            // Destroying is valid even after a device loss, so teardown continues whatever the wait returns.
            VkResult idle = vulkan.WaitIdle();
            if (idle != VkResult.Success)
            {
                _vulkanLog.Error($"vkDeviceWaitIdle returned {idle} during teardown.");
            }

            swapchain?.Destroy();
            splashImage?.Dispose();
        }

        double seconds = clock.Elapsed.TotalSeconds;
        _log.Information($"Presented {presented} frames in {seconds:F2} s ({presented / seconds:F1} fps); "
            + $"swapchain recreations {recreations}, out-of-date {outOfDate}, suboptimal {suboptimal}; "
            + $"{steadyBytes} managed bytes allocated over {steadyFrames} steady-state loop iterations.");
        _log.Information($"{splashStatistics.Format(Stopwatch.Frequency)}");
        if (_options.ExerciseResize && recreations == 0)
        {
            throw new SampleFailureException("--exercise-resize: the swapchain was never recreated.");
        }
    }

    // Chooses how this swapchain shows the splash, and uploads the logo in its format if that changed. Runs with the
    // device idle (right after a swapchain was created), so replacing the image is safe. On the copy path, it also stops
    // the window from shrinking below the logo for as long as the splash lasts (SplashWindowLimits).
    private SplashDraw PrepareSplash(
        SdlWindow window, VulkanContext vulkan, Swapchain swapchain, SplashLogo logo, ref SplashImage? image, ref bool minimumSizeSet)
    {
        VkFormatProperties formatProperties = vulkan.PhysicalDevice.GetFormatProperties(swapchain.Format.Format);
        PixelRect destination = SplashLayout.Fit(logo.Size, new PixelSize(swapchain.Extent.Width, swapchain.Extent.Height), SplashMargin);
        SplashPlan plan = SplashCapabilities.Choose(
            new SplashSurfaceSupport(swapchain.Format, swapchain.Usage, formatProperties.OptimalTilingFeatures), logo.Size, destination);
        if (!plan.IsSupported)
        {
            // The splash is a licence duty: never skip it silently.
            throw new SampleFailureException(plan.FailureMessage ?? "The splash can't be drawn on this surface.");
        }

        if (image is null || image.Format != plan.LogoFormat)
        {
            image?.Dispose();
            image = null;
            image = SplashImage.Upload(vulkan, logo, plan);
        }

        _log.Information($"Splash: {plan.Transfer} of the {logo.Size} logo to {destination} ({plan.LogoFormat})");
        if (plan.Transfer == SplashTransfer.Copy && !minimumSizeSet)
        {
            float density = Sdl.Video.GetWindowPixelDensity(window);
            if (density <= 0)
            {
                throw SdlException.FromError("SDL_GetWindowPixelDensity failed");
            }

            PixelSize minimum = SplashWindowLimits.MinimumWindowSize(plan.Transfer, logo.Size, SplashMargin, density);
            NativeCheck.Sdl(Sdl.Video.SetWindowMinimumSize(window, minimum.Width, minimum.Height), "SDL_SetWindowMinimumSize");
            minimumSizeSet = true;
            _log.Information($"Splash: window minimum size {minimum} while the splash lasts (1:1 copy)");
        }

        return new SplashDraw(image.Image, image.Size, plan.Transfer, destination);
    }

    // Returns true if it resized the window in this iteration.
    private bool ExerciseResize(SdlWindow window, int presented)
    {
        // Shrink at a third of the run and restore at two thirds: two size changes, each needing a new swapchain.
        if (!_options.ExerciseResize || _options.Frames is not { } frames)
        {
            return false;
        }

        if ((_resizes == 0 && presented >= frames / 3) || (_resizes == 1 && presented >= 2 * frames / 3))
        {
            bool shrink = _resizes++ == 0;
            int width = shrink ? Math.Max(1, _options.Width * 3 / 4) : _options.Width;
            int height = shrink ? Math.Max(1, _options.Height * 3 / 4) : _options.Height;
            _log.Information($"Resizing the window to {width}x{height} (at frame {presented})");
            NativeCheck.Sdl(Sdl.Video.SetWindowSize(window, width, height), "SDL_SetWindowSize");
            return true;
        }

        return false;
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
}
