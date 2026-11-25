using System;
using System.IO;
using System.Runtime.InteropServices;
using Brutal.SdlApi;
using Brutal.VulkanApi;
using BrutalEngine.Core.Logging;

namespace HelloWindow;

/// <summary>
/// Makes a published macOS build self-contained: it uses the Khronos loader and MoltenVK shipped next to the
/// executable, with no Vulkan SDK, Homebrew or environment variables.
/// </summary>
/// <remarks>
/// <para>
/// BRUTAL's resolver (Brutal.Vulkan/ImportResolver.cs:50-57) looks for <c>libvulkan.1.dylib</c> by bare name and in
/// Homebrew's folders, never in the app directory, and SDL's own search differs from it. So the bundled loader is
/// preloaded by absolute path, and SDL is pointed at the same file, so both use one loader (spike S1,
/// docs/spikes/README.md). The loader doesn't look for drivers next to itself either, so <c>VK_DRIVER_FILES</c> is
/// set to the bundled MoltenVK manifest, unless the user already chose drivers.
/// </para>
/// <para>
/// <see cref="TryConfigure"/> must run before anything touches BRUTAL's <c>Vulkan</c> class, and before the first
/// Vulkan instance call. It runs once, on the main thread, at startup.
/// </para>
/// </remarks>
internal static partial class BundledVulkan
{
    /// <summary>The bundled Khronos loader's file name.</summary>
    public const string LoaderFileName = "libvulkan.1.dylib";

    /// <summary>The bundled MoltenVK ICD manifest's file name. Its <c>library_path</c> is relative to the manifest.</summary>
    public const string IcdFileName = "MoltenVK_icd.json";

    /// <summary>The loader's variable that lists the only driver manifests it loads.</summary>
    public const string DriverFilesVariable = "VK_DRIVER_FILES";

    /// <summary>The deprecated name of <see cref="DriverFilesVariable"/>, which the loader still honours.</summary>
    public const string LegacyDriverFilesVariable = "VK_ICD_FILENAMES";

    /// <summary>What <see cref="TryConfigure"/> needs from the process. Tests substitute a fake.</summary>
    internal interface IHost
    {
        /// <summary>Gets a value indicating whether the process runs on macOS.</summary>
        public bool IsMacOS { get; }

        /// <summary>Returns whether a file exists.</summary>
        /// <param name="path">An absolute path.</param>
        /// <returns><see langword="true"/> if the file exists.</returns>
        public bool FileExists(string path);

        /// <summary>Reads an environment variable.</summary>
        /// <param name="name">The variable.</param>
        /// <returns>Its value, or null if it is unset.</returns>
        public string? GetEnvironmentVariable(string name);

        /// <summary>Sets an environment variable for the rest of the process, where native code reads it too.</summary>
        /// <param name="name">The variable.</param>
        /// <param name="value">The value.</param>
        public void SetEnvironmentVariable(string name, string value);

        /// <summary>Preloads a Vulkan loader so that BRUTAL and SDL both use it.</summary>
        /// <param name="path">The loader's absolute path.</param>
        public void PreloadLoader(string path);
    }

    /// <summary>Uses the Vulkan natives bundled in <paramref name="appDirectory"/>, on macOS only.</summary>
    /// <typeparam name="THost">The process abstraction.</typeparam>
    /// <param name="appDirectory">The executable's directory (<see cref="AppContext.BaseDirectory"/>).</param>
    /// <param name="explicitLoader">The <c>--vulkan-loader</c> path, or null. When given, the caller preloads it and the
    /// bundled loader is left alone.</param>
    /// <param name="host">The process abstraction.</param>
    /// <returns><see langword="true"/> if anything bundled was put to use; <see langword="false"/> if nothing was
    /// bundled, or the OS isn't macOS.</returns>
    /// <exception cref="ArgumentException"><paramref name="appDirectory"/> is not a fully qualified path.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="host"/> is null.</exception>
    public static bool TryConfigure<THost>(string appDirectory, string? explicitLoader, THost host)
        where THost : IHost
    {
        ArgumentNullException.ThrowIfNull(appDirectory);
        if (host is null)
        {
            throw new ArgumentNullException(nameof(host));
        }

        if (!Path.IsPathFullyQualified(appDirectory))
        {
            throw new ArgumentException($"The app directory must be a fully qualified path: '{appDirectory}'.", nameof(appDirectory));
        }

        if (!host.IsMacOS)
        {
            return false;
        }

        // The driver first: the loader reads VK_DRIVER_FILES whenever it enumerates drivers, so this must precede any
        // instance-level Vulkan call, and nothing is lost by doing it before the loader is even loaded.
        bool configured = false;
        string icd = Path.Join(appDirectory, IcdFileName);
        if (string.IsNullOrEmpty(host.GetEnvironmentVariable(DriverFilesVariable))
            && string.IsNullOrEmpty(host.GetEnvironmentVariable(LegacyDriverFilesVariable))
            && host.FileExists(icd))
        {
            host.SetEnvironmentVariable(DriverFilesVariable, icd);
            configured = true;
        }

        string loader = Path.Join(appDirectory, LoaderFileName);
        if (explicitLoader is null && host.FileExists(loader))
        {
            host.PreloadLoader(loader);
            configured = true;
        }

        return configured;
    }

    /// <summary>Loads the Vulkan loader at <paramref name="path"/>, checks that BRUTAL uses it, and points SDL at it.</summary>
    /// <param name="path">The loader's absolute path.</param>
    /// <param name="origin">Where the path came from, for the log line.</param>
    /// <param name="log">Where the loader is logged.</param>
    /// <exception cref="DllNotFoundException">The library could not be loaded.</exception>
    /// <exception cref="EntryPointNotFoundException">The library is not a Vulkan loader.</exception>
    /// <exception cref="SampleFailureException">BRUTAL had already resolved a different loader.</exception>
    /// <exception cref="SdlException">SDL rejected the hint.</exception>
    /// <remarks>Must run before anything touches BRUTAL's <c>Vulkan</c> class.</remarks>
    public static unsafe void PreloadLoader(string path, string origin, LogCategory log)
    {
        // BRUTAL looks the loader up by bare name, which binds to a library of that name already in the process
        // (spike S1, docs/spikes/README.md). Check it really did, rather than silently using another loader.
        nint library = NativeLibrary.Load(path);
        nint entryPoint = NativeLibrary.GetExport(library, "vkGetInstanceProcAddr");

        // Sound: only the function pointer's address is compared; it is never called through this cast.
        if ((nint)Vulkan.GetInstanceProcAddr.Invoke != entryPoint)
        {
            throw new SampleFailureException($"BRUTAL resolved a different Vulkan loader than {path}.");
        }

        NativeCheck.Sdl(Sdl.PInvoke.SetHint(Sdl.Constants.HintVulkanLibrary, path) != 0, "SDL_SetHint(SDL_VULKAN_LIBRARY)");
        log.Information($"Vulkan loader: {path} ({origin}; preloaded, used by BRUTAL and SDL)");
    }

    /// <summary>The real process: the file system, the C runtime's environment and <see cref="PreloadLoader"/>.</summary>
    /// <param name="log">Where the environment it sets and the loader it preloads are logged.</param>
    internal readonly partial struct ProcessHost(LogCategory log) : IHost
    {
        /// <inheritdoc/>
        public bool IsMacOS => OperatingSystem.IsMacOS();

        /// <inheritdoc/>
        public bool FileExists(string path) => File.Exists(path);

        /// <inheritdoc/>
        public string? GetEnvironmentVariable(string name) => Environment.GetEnvironmentVariable(name);

        /// <inheritdoc/>
        /// <exception cref="PlatformNotSupportedException">The OS isn't macOS.</exception>
        /// <exception cref="SampleFailureException"><c>setenv</c> failed.</exception>
        public void SetEnvironmentVariable(string name, string value)
        {
            if (!OperatingSystem.IsMacOS())
            {
                throw new PlatformNotSupportedException("Setting the native environment is only implemented on macOS.");
            }

            // .NET keeps its own copy of the environment on Unix, so Environment.SetEnvironmentVariable is invisible to
            // the Vulkan loader's getenv. Set the C runtime's copy, then .NET's, so both agree.
            if (SetEnv(name, value, overwrite: 1) != 0)
            {
                throw new SampleFailureException($"setenv({name}) failed: {Marshal.GetLastPInvokeErrorMessage()}");
            }

            Environment.SetEnvironmentVariable(name, value);
            log.Information($"{name}={value} (bundled driver)");
        }

        /// <inheritdoc/>
        public void PreloadLoader(string path) => BundledVulkan.PreloadLoader(path, "bundled", log);

        // libSystem holds the C runtime on macOS. An absolute path, so neither the JIT nor NativeAOT has to probe for it.
        [LibraryImport("/usr/lib/libSystem.B.dylib", EntryPoint = "setenv", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
        private static partial int SetEnv(string name, string value, int overwrite);
    }
}
