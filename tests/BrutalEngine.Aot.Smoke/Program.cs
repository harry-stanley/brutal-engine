using System;
using System.Runtime.InteropServices;

namespace BrutalEngine.Aot.Smoke;

/// <summary>
/// Entry point for the native-stack smoke harness.
/// </summary>
/// <remarks>
/// Usage: <c>BrutalEngine.Aot.Smoke vulkan-offscreen [--require-cpu] [--inject-validation-error]</c>.
/// Exit codes are listed in <see cref="ExitCode"/>.
/// </remarks>
internal static class Program
{
    private const string Usage =
        "Usage: BrutalEngine.Aot.Smoke vulkan-offscreen [--require-cpu] [--inject-validation-error]";

    private static int Main(string[] args)
    {
        Console.WriteLine($"BrutalEngine.Aot.Smoke on {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
        Console.WriteLine($".NET {Environment.Version}");

        if (args.Length == 0 || args[0] != "vulkan-offscreen")
        {
            Console.Error.WriteLine(Usage);
            return (int)ExitCode.Usage;
        }

        bool requireCpu = false;
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
                default:
                    Console.Error.WriteLine($"Unknown option '{args[i]}'.");
                    Console.Error.WriteLine(Usage);
                    return (int)ExitCode.Usage;
            }
        }

        try
        {
            ExitCode result = VulkanOffscreenScenario.Run(requireCpu, injectValidationError);
            Console.WriteLine(result == ExitCode.Pass ? "PASS" : $"FAIL ({result})");
            return (int)result;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Console.WriteLine($"FAIL (setup): {exception}");
            return (int)ExitCode.SetupFailure;
        }
    }
}

/// <summary>Process exit codes of the smoke harness. CI asserts on these exact values.</summary>
internal enum ExitCode
{
    /// <summary>Everything worked and validation reported nothing.</summary>
    Pass = 0,

    /// <summary>Rendering completed but the read-back pixels were wrong.</summary>
    PixelMismatch = 1,

    /// <summary>The Khronos validation layer reported at least one error or warning.</summary>
    ValidationMessages = 2,

    /// <summary>Setup failed: no suitable device, missing layer, or an exception.</summary>
    SetupFailure = 3,

    /// <summary>Bad command line.</summary>
    Usage = 64,
}
