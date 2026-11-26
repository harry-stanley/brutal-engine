using BrutalEngine.Core;

namespace BrutalEngine.Graphics.Vulkan.Loader;

/// <summary>
/// What <see cref="LoaderSetup.Configure"/> needs from the process (docs/design/graphics-vulkan.md section 2). The real
/// host is the <c>ProcessLoaderHost</c> struct; tests substitute a fake, so every branch of the loader decision, the macOS
/// ones included, runs on any OS.
/// </summary>
/// <remarks>
/// Every member runs on the main thread, before the platform, any window or any other thread starts: <c>setenv</c>
/// isn't thread-safe against a concurrent <c>getenv</c>, and the Vulkan loader reads the driver variables when it first
/// enumerates drivers. Each effect returns a <see cref="Result"/> or a value; nothing is stored in the setup.
/// </remarks>
internal interface IVulkanLoaderHost
{
    /// <summary>Gets a value indicating whether the process runs on macOS, the only OS with a bundled loader.</summary>
    public bool IsMacOS { get; }

    /// <summary>Returns whether a file exists.</summary>
    /// <param name="path">An absolute path.</param>
    /// <returns><see langword="true"/> if the file exists.</returns>
    public bool FileExists(string path);

    /// <summary>Reads an environment variable.</summary>
    /// <param name="name">The variable.</param>
    /// <returns>Its value, or <see langword="null"/> if it is unset.</returns>
    public string? GetEnvironmentVariable(string name);

    /// <summary>
    /// Sets an environment variable for the rest of the process, in the C runtime's environment (where the Vulkan loader
    /// reads it) and then in .NET's copy, so both agree.
    /// </summary>
    /// <param name="name">The variable.</param>
    /// <param name="value">The value.</param>
    /// <returns>Success, or an <see cref="ErrorKind.Native"/> failure when <c>setenv</c> failed.</returns>
    public Result SetEnvironmentVariable(string name, string value);

    /// <summary>
    /// Loads a Vulkan loader by absolute path and keeps it loaded for the process lifetime, so BRUTAL's bare-name lookup
    /// binds to it.
    /// </summary>
    /// <param name="path">The loader's absolute path.</param>
    /// <returns>
    /// The address of the library's <c>vkGetInstanceProcAddr</c> export, or an <see cref="ErrorKind.Unavailable"/>
    /// failure when the library can't be loaded or isn't a Vulkan loader.
    /// </returns>
    public Result<nint> Preload(string path);

    /// <summary>
    /// Returns the <c>vkGetInstanceProcAddr</c> that BRUTAL's <c>Vulkan</c> class bound. The first call runs BRUTAL's
    /// loader resolution, which binds for the process lifetime.
    /// </summary>
    /// <returns>The function's address, or 0 when BRUTAL found no loader.</returns>
    public nint BrutalGetInstanceProcAddr();
}
