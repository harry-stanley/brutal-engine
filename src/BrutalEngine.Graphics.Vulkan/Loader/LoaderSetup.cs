using System;
using System.IO;
using BrutalEngine.Core;

namespace BrutalEngine.Graphics.Vulkan.Loader;

/// <summary>
/// Decides which Vulkan loader and drivers the process uses, preloads the loader when it isn't the system's, and checks
/// that BRUTAL bound it (docs/design/graphics-vulkan.md section 2). Ports the decisions of M0's
/// <c>samples/HelloWindow/BundledVulkan.cs</c> unchanged: the driver variable first, the user's choice respected.
/// </summary>
/// <remarks>
/// <para>
/// Windows and Linux use BRUTAL's own search unless an explicit loader is given, and their drivers are never touched.
/// On macOS (unverified, no CI) the loader bundled in the app directory is preloaded by absolute path, because BRUTAL
/// never searches there, and <c>VK_DRIVER_FILES</c> is set to the bundled MoltenVK manifest unless the user already set
/// <c>VK_DRIVER_FILES</c> or <c>VK_ICD_FILENAMES</c>. An explicit loader is preloaded and checked the same way on
/// every OS.
/// </para>
/// <para>
/// Stateless: the decision is returned, not stored, and repeating a call with the same inputs repeats the decision (an
/// already-set variable is left alone; preloading the same path again returns the same library). Runs on the main
/// thread before the platform or any other thread starts (<see cref="IVulkanLoaderHost"/>). Allocates only the
/// manifest and loader paths it builds on macOS and the messages of failures.
/// </para>
/// </remarks>
internal static class LoaderSetup
{
    /// <summary>The bundled Khronos loader's file name (macOS).</summary>
    public const string LoaderFileName = "libvulkan.1.dylib";

    /// <summary>The bundled MoltenVK driver manifest's file name (macOS).</summary>
    public const string IcdFileName = "MoltenVK_icd.json";

    /// <summary>The loader's variable that lists the only driver manifests it loads.</summary>
    public const string DriverFilesVariable = "VK_DRIVER_FILES";

    /// <summary>The deprecated name of <see cref="DriverFilesVariable"/>, which the loader still honours.</summary>
    public const string LegacyDriverFilesVariable = "VK_ICD_FILENAMES";

    /// <summary>Decides the loader and drivers, preloads the loader if needed, and checks that BRUTAL bound it.</summary>
    /// <typeparam name="THost">The process abstraction; the real one is a struct, so nothing is boxed.</typeparam>
    /// <param name="appDirectory">The app's directory, fully qualified: where bundled natives would be.</param>
    /// <param name="explicitLoader">A fully qualified loader path chosen by the host, or <see langword="null"/>.</param>
    /// <param name="host">The process abstraction.</param>
    /// <returns>
    /// The decision, or a failure: the host's <see cref="ErrorKind.Native"/> error when setting the driver variable
    /// failed; the host's <see cref="ErrorKind.Unavailable"/> error when the chosen loader couldn't be preloaded;
    /// <see cref="ErrorKind.Unavailable"/> ("BRUTAL resolved a different Vulkan loader than &lt;path&gt;") when BRUTAL
    /// bound another loader than the preloaded one, never a fallback; and the host's <see cref="ErrorKind.Unavailable"/>
    /// error ("Vulkan loader missing: " and BRUTAL's message) when BRUTAL found no loader, passed through unchanged.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="appDirectory"/> or <paramref name="host"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="appDirectory"/>, or a non-null <paramref name="explicitLoader"/>, is not a fully qualified path.
    /// </exception>
    public static Result<LoaderDecision> Configure<THost>(string appDirectory, string? explicitLoader, THost host)
        where THost : IVulkanLoaderHost
    {
        ArgumentNullException.ThrowIfNull(appDirectory);

        // The type test keeps a struct host from being boxed for the null check in unoptimised code.
        if (!typeof(THost).IsValueType && host is null)
        {
            throw new ArgumentNullException(nameof(host));
        }

        if (!Path.IsPathFullyQualified(appDirectory))
        {
            throw new ArgumentException("The app directory must be a fully qualified path.", nameof(appDirectory));
        }

        if (explicitLoader is not null && !Path.IsPathFullyQualified(explicitLoader))
        {
            throw new ArgumentException("An explicit Vulkan loader must be a fully qualified path.", nameof(explicitLoader));
        }

        string? loader = explicitLoader;
        LoaderOrigin origin = explicitLoader is null ? LoaderOrigin.System : LoaderOrigin.Explicit;
        string? driverFiles = null;
        if (host.IsMacOS)
        {
            // The driver first: the loader reads VK_DRIVER_FILES whenever it enumerates drivers, and the variable is
            // independent of which loader is used.
            Result<string?> drivers = ConfigureDrivers(appDirectory, host);
            if (!drivers.TryGetValue(out driverFiles))
            {
                return drivers.Error;
            }

            string bundled = Path.Join(appDirectory, LoaderFileName);
            if (loader is null && host.FileExists(bundled))
            {
                loader = bundled;
                origin = LoaderOrigin.Bundled;
            }
        }

        Result<nint> bound;
        if (loader is null)
        {
            bound = host.BrutalGetInstanceProcAddr();
            return bound.TryGetError(out EngineError missing) ? missing : new LoaderDecision(null, LoaderOrigin.System, driverFiles);
        }

        // BRUTAL looks the loader up by bare name, which binds to a library of that name already in the process. Check
        // that it really did, rather than silently using another loader.
        Result<nint> preloaded = host.Preload(loader);
        if (!preloaded.TryGetValue(out nint entryPoint))
        {
            return preloaded.Error;
        }

        bound = host.BrutalGetInstanceProcAddr();
        if (!bound.TryGetValue(out nint brutalEntryPoint))
        {
            return bound.Error;
        }

        return brutalEntryPoint == entryPoint
            ? new LoaderDecision(loader, origin, driverFiles)
            : new EngineError(ErrorKind.Unavailable, "BRUTAL resolved a different Vulkan loader than " + loader);
    }

    /// <summary>
    /// macOS: returns the driver manifests the loader will read, setting <see cref="DriverFilesVariable"/> to the bundled
    /// manifest when the user set neither variable and the manifest exists.
    /// </summary>
    private static Result<string?> ConfigureDrivers<THost>(string appDirectory, THost host)
        where THost : IVulkanLoaderHost
    {
        string? chosen = host.GetEnvironmentVariable(DriverFilesVariable);
        if (string.IsNullOrEmpty(chosen))
        {
            chosen = host.GetEnvironmentVariable(LegacyDriverFilesVariable);
        }

        if (!string.IsNullOrEmpty(chosen))
        {
            return chosen;
        }

        string icd = Path.Join(appDirectory, IcdFileName);
        if (!host.FileExists(icd))
        {
            return (string?)null;
        }

        Result set = host.SetEnvironmentVariable(DriverFilesVariable, icd);
        return set.TryGetError(out EngineError error) ? error : icd;
    }
}
