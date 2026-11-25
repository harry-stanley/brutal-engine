using System;
using System.Runtime.InteropServices;
using Brutal.SdlApi;
using Brutal.VulkanApi;

namespace HelloWindow;

/// <summary>Runs the sample: SDL and Vulkan setup, and teardown in reverse order.</summary>
internal sealed unsafe class HelloWindowApp
{
    private readonly HelloWindowOptions _options;
    private bool _setupComplete;

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
            _setupComplete = true;
        }
        finally
        {
            Sdl.Init.Quit();
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
