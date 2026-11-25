using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using Brutal.VulkanApi;

namespace HelloWindow;

/// <summary>Command-line options of the sample. Create with <see cref="TryParse"/>.</summary>
internal sealed record HelloWindowOptions
{
    /// <summary>The usage text printed for a bad command line.</summary>
    public const string Usage =
        """
        Usage: HelloWindow [options]
          --headless                 use SDL's offscreen video driver (no visible window); needs --frames
          --frames <n>               exit after n presented frames (default: run until the window is closed)
          --size <w>x<h>             initial window size in pixels (default 1280x720)
          --validation               enable the Khronos validation layer; any message fails the run (exit 2)
          --exercise-resize          resize the window twice during the run; needs --frames
          --require-cpu              accept only a CPU (software) device, e.g. lavapipe
          --require-driver <id>      accept only a device whose VkDriverId has this name, e.g. Moltenvk
          --vulkan-loader <path>     load the Vulkan loader from this absolute path before BRUTAL looks for one
          --inject-validation-error  record an invalid barrier on purpose (implies --validation; must exit 2)
        """;

    // Far above any real window, and small enough that width * height * 4 bytes can't overflow an int.
    private const int MaxDimension = 16384;

    /// <summary>Gets a value indicating whether to use SDL's offscreen video driver.</summary>
    public bool Headless { get; init; }

    /// <summary>Gets the number of frames to present before exiting, or null to run until the window closes.</summary>
    public int? Frames { get; init; }

    /// <summary>Gets the initial window width in pixels.</summary>
    public int Width { get; init; } = 1280;

    /// <summary>Gets the initial window height in pixels.</summary>
    public int Height { get; init; } = 720;

    /// <summary>Gets a value indicating whether the Khronos validation layer is enabled.</summary>
    public bool Validation { get; init; }

    /// <summary>Gets a value indicating whether to resize the window twice during the run.</summary>
    public bool ExerciseResize { get; init; }

    /// <summary>Gets a value indicating whether only a CPU (software) device is acceptable.</summary>
    public bool RequireCpu { get; init; }

    /// <summary>Gets the only acceptable driver, or null to accept any driver.</summary>
    public VkDriverId? RequireDriver { get; init; }

    /// <summary>Gets the absolute path of the Vulkan loader to preload, or null to let BRUTAL find one.</summary>
    public string? VulkanLoader { get; init; }

    /// <summary>Gets a value indicating whether to record a wrong image layout on purpose.</summary>
    public bool InjectValidationError { get; init; }

    /// <summary>Parses the command line.</summary>
    /// <param name="args">The arguments, without the program name.</param>
    /// <param name="options">The parsed options, or null on failure.</param>
    /// <param name="error">Why parsing failed, or empty on success.</param>
    /// <returns><see langword="true"/> if <paramref name="args"/> is a valid command line.</returns>
    public static bool TryParse(ReadOnlySpan<string> args, [NotNullWhen(true)] out HelloWindowOptions? options, out string error)
    {
        options = null;
        error = string.Empty;
        var parsed = new HelloWindowOptions();
        for (int i = 0; i < args.Length; i++)
        {
            string option = args[i];
            if (option is "--frames" or "--size" or "--require-driver" or "--vulkan-loader" && i + 1 == args.Length)
            {
                error = $"{option} needs a value.";
                return false;
            }

            switch (option)
            {
                case "--headless":
                    parsed = parsed with { Headless = true };
                    break;
                case "--validation":
                    parsed = parsed with { Validation = true };
                    break;
                case "--exercise-resize":
                    parsed = parsed with { ExerciseResize = true };
                    break;
                case "--require-cpu":
                    parsed = parsed with { RequireCpu = true };
                    break;
                case "--inject-validation-error":
                    parsed = parsed with { InjectValidationError = true, Validation = true };
                    break;
                case "--frames":
                    if (!TryParsePositive(args[++i], out int frames))
                    {
                        error = $"--frames needs a positive whole number, got '{args[i]}'.";
                        return false;
                    }

                    parsed = parsed with { Frames = frames };
                    break;
                case "--size":
                    string[] parts = args[++i].Split('x');
                    if (parts.Length != 2 || !TryParsePositive(parts[0], out int width) || !TryParsePositive(parts[1], out int height)
                        || width > MaxDimension || height > MaxDimension)
                    {
                        error = $"--size needs <width>x<height>, each 1 to {MaxDimension}, got '{args[i]}'.";
                        return false;
                    }

                    parsed = parsed with { Width = width, Height = height };
                    break;
                case "--require-driver":
                    string name = args[++i];

                    // Names only: Enum.TryParse also accepts numbers, so a typo could otherwise match a real ID.
                    if (name.Length == 0 || !char.IsAsciiLetter(name[0])
                        || !Enum.TryParse(name, ignoreCase: true, out VkDriverId driver) || !Enum.IsDefined(driver))
                    {
                        error = $"'{name}' is not a VkDriverId name.";
                        return false;
                    }

                    parsed = parsed with { RequireDriver = driver };
                    break;
                case "--vulkan-loader":
                    string path = args[++i];
                    if (!Path.IsPathFullyQualified(path))
                    {
                        error = $"--vulkan-loader needs an absolute path, got '{path}'.";
                        return false;
                    }

                    parsed = parsed with { VulkanLoader = path };
                    break;
                default:
                    error = $"Unknown option '{option}'.";
                    return false;
            }
        }

        if (parsed.Frames is null && (parsed.Headless || parsed.ExerciseResize))
        {
            error = parsed.Headless ? "--headless needs --frames: a headless window can't be closed." : "--exercise-resize needs --frames.";
            return false;
        }

        options = parsed;
        return true;
    }

    private static bool TryParsePositive(string text, out int value) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value > 0;
}
