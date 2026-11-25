using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.InteropServices;
using Brutal.VulkanApi;

namespace BrutalEngine.Aot.Smoke;

/// <summary>
/// Entry point for the native-stack smoke harness.
/// </summary>
/// <remarks>
/// Usage: <c>BrutalEngine.Aot.Smoke vulkan-offscreen [options]</c>; the options are listed in the usage text.
/// Exit codes are listed in <see cref="ExitCode"/>.
/// </remarks>
internal static class Program
{
    private const string Usage =
        """
        Usage: BrutalEngine.Aot.Smoke vulkan-offscreen [options]
          --require-cpu              accept only a CPU (software) device, e.g. lavapipe
          --require-driver <id>      accept only a device whose VkDriverId has this name, e.g. Moltenvk
          --vulkan-loader <path>     load the Vulkan loader from this absolute path before BRUTAL looks for one
          --inject-validation-error  record an invalid barrier on purpose; validation must report it (exit 2)
        """;

    private static int Main(string[] args)
    {
        Console.WriteLine($"BrutalEngine.Aot.Smoke on {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
        Console.WriteLine($".NET {Environment.Version}");

        if (!TryParse(args, out SmokeOptions? options, out string error))
        {
            Console.Error.WriteLine(error);
            Console.Error.WriteLine(Usage);
            return (int)ExitCode.Usage;
        }

        try
        {
            ExitCode result = VulkanOffscreenScenario.Run(options);
            Console.WriteLine(result == ExitCode.Pass ? "PASS" : $"FAIL ({result})");
            return (int)result;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Console.WriteLine($"FAIL (setup): {exception}");
            return (int)ExitCode.SetupFailure;
        }
    }

    private static bool TryParse(string[] args, [NotNullWhen(true)] out SmokeOptions? options, out string error)
    {
        options = null;
        error = string.Empty;
        if (args.Length == 0 || args[0] != "vulkan-offscreen")
        {
            error = "Expected the scenario name 'vulkan-offscreen'.";
            return false;
        }

        bool requireCpu = false;
        VkDriverId? requireDriver = null;
        string? vulkanLoader = null;
        bool injectValidationError = false;
        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--require-cpu":
                    requireCpu = true;
                    break;
                case "--inject-validation-error":
                    injectValidationError = true;
                    break;
                case "--require-driver" when i + 1 < args.Length:
                    string name = args[++i];

                    // Names only: Enum.TryParse also accepts numbers, so a typo could otherwise match a real ID.
                    if (name.Length == 0 || !char.IsAsciiLetter(name[0])
                        || !Enum.TryParse(name, ignoreCase: true, out VkDriverId driver) || !Enum.IsDefined(driver))
                    {
                        error = $"'{name}' is not a VkDriverId name.";
                        return false;
                    }

                    requireDriver = driver;
                    break;
                case "--vulkan-loader" when i + 1 < args.Length:
                    vulkanLoader = args[++i];
                    if (!Path.IsPathFullyQualified(vulkanLoader))
                    {
                        error = $"--vulkan-loader needs an absolute path, got '{vulkanLoader}'.";
                        return false;
                    }

                    break;
                default:
                    error = $"Unknown option or missing value: '{args[i]}'.";
                    return false;
            }
        }

        options = new SmokeOptions(requireCpu, requireDriver, vulkanLoader, injectValidationError);
        return true;
    }
}

/// <summary>Command-line options of the <c>vulkan-offscreen</c> scenario.</summary>
/// <param name="RequireCpu">Accept only a CPU (software) device, to prove CI ran on lavapipe.</param>
/// <param name="RequireDriver">Accept only a device with this driver ID (for example MoltenVK), or any driver if null.</param>
/// <param name="VulkanLoader">Absolute path of the Vulkan loader to preload, or null to let BRUTAL find one.</param>
/// <param name="InjectValidationError">Record a wrong image layout on purpose, so validation must report it.</param>
internal sealed record SmokeOptions(bool RequireCpu, VkDriverId? RequireDriver, string? VulkanLoader, bool InjectValidationError);

/// <summary>Process exit codes of the smoke harness. CI asserts on these exact values.</summary>
internal enum ExitCode
{
    /// <summary>Everything worked and validation reported nothing.</summary>
    Pass = 0,

    /// <summary>Rendering completed but the read-back pixels were wrong.</summary>
    PixelMismatch = 1,

    /// <summary>The Khronos validation layer reported at least one error or warning.</summary>
    ValidationMessages = 2,

    /// <summary>Setup failed: no suitable device, missing layer, wrong loader, or an exception.</summary>
    SetupFailure = 3,

    /// <summary>Bad command line.</summary>
    Usage = 64,
}
