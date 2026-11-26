using System;
using System.IO;
using System.Text;
using BrutalEngine.Core;
using BrutalEngine.Core.Logging;
using BrutalEngine.Core.Logging.Sinks;
using BrutalEngine.Graphics.Vulkan;

namespace BrutalEngine.Aot.Smoke;

/// <summary>
/// Creates the engine's Vulkan backend, which settles the Vulkan loader for the process, and checks its result and its
/// one logged decision (docs/design/graphics-vulkan.md section 14). Run under the JIT and as a NativeAOT executable,
/// it proves <c>VulkanBackend.Create</c> and the loader setup behind it in both.
/// </summary>
/// <remarks>
/// Takes no options except <c>--vulkan-loader &lt;path&gt;</c>, which becomes <c>VulkanBackendOptions.LoaderPath</c>.
/// Creates no instance or device, so it needs a loader but no driver or validation layer.
/// </remarks>
internal static class GraphicsVulkanScenario
{
    /// <summary>The scenario's name on the command line.</summary>
    public const string Name = "graphics-vulkan";

    /// <summary>Parses the scenario's options.</summary>
    /// <param name="args">The arguments after the scenario name.</param>
    /// <param name="loaderPath">The explicit loader, or null.</param>
    /// <param name="error">Why parsing failed, or empty.</param>
    /// <returns><see langword="true"/> if the options are valid.</returns>
    public static bool TryParse(ReadOnlySpan<string> args, out string? loaderPath, out string error)
    {
        loaderPath = null;
        error = string.Empty;
        if (args.Length == 0)
        {
            return true;
        }

        if (args.Length == 2 && args[0] == "--vulkan-loader" && Path.IsPathFullyQualified(args[1]))
        {
            loaderPath = args[1];
            return true;
        }

        error = $"{Name} takes no options except --vulkan-loader <absolute path>, got '{string.Join(' ', args.ToArray())}'.";
        return false;
    }

    /// <summary>Runs the scenario.</summary>
    /// <param name="loaderPath">The explicit loader, or null for the backend's own choice.</param>
    /// <returns>The outcome: <see cref="ExitCode.Pass"/>, or <see cref="ExitCode.SetupFailure"/>.</returns>
    public static ExitCode Run(string? loaderPath)
    {
        MemoryLogSink sink = new(16, 4096);
        using Logger logger = new(new LoggerOptions { Sinks = [sink] });
        Result<VulkanBackend> created = VulkanBackend.Create(
            new VulkanBackendOptions { AppDirectory = AppContext.BaseDirectory, Logger = logger, LoaderPath = loaderPath });

        var records = new MemoryLogRecord[sink.CapacityEntries];
        byte[] messages = new byte[sink.CapacityBytes];
        int count = sink.CopyTo(records, messages);
        for (int i = 0; i < count; i++)
        {
            Console.WriteLine($"log: {records[i].Category}: {Encoding.UTF8.GetString(records[i].Message.Span)}");
        }

        if (!created.TryGetValue(out VulkanBackend? backend))
        {
            Console.WriteLine($"VulkanBackend.Create failed: {created.Error}");
            return ExitCode.SetupFailure;
        }

        Console.WriteLine($"Backend: {backend.Name}, LoaderPath: {backend.LoaderPath ?? "null (system loader)"}");

        // An explicit loader must come back unchanged. Without one, Windows and Linux use the system loader (null);
        // macOS (unverified) may report a bundled one, which the smoke harness doesn't ship.
        if (backend.Name != "Vulkan" || (loaderPath is not null && backend.LoaderPath != loaderPath))
        {
            Console.WriteLine($"Unexpected backend: expected Vulkan with LoaderPath {loaderPath ?? "null"}.");
            return ExitCode.SetupFailure;
        }

        if (count != 1
            || records[0].Level != LogLevel.Information
            || records[0].Category != "Graphics.Vulkan"
            || !records[0].Message.Span.StartsWith("Vulkan loader: "u8))
        {
            Console.WriteLine($"Expected exactly one Information entry \"Vulkan loader: ...\" in Graphics.Vulkan, got {count} entries.");
            return ExitCode.SetupFailure;
        }

        return ExitCode.Pass;
    }
}
