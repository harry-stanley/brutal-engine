using System;
using System.IO;
using System.Runtime.InteropServices;
using BrutalEngine.Core;

namespace BrutalEngine.Graphics.Vulkan.Loader;

/// <summary>
/// The real process for <see cref="LoaderSetup.Configure"/>: the file system, the environment, .NET's native library
/// loader and BRUTAL's <c>Vulkan</c> class. A <see langword="readonly"/> struct with no fields, so the generic
/// <c>Configure</c> never boxes it.
/// </summary>
/// <remarks>
/// Main thread only, before the platform or any other thread starts (<see cref="IVulkanLoaderHost"/>). A preloaded
/// library is never freed: BRUTAL binds it for the process lifetime.
/// </remarks>
internal readonly partial struct ProcessLoaderHost : IVulkanLoaderHost
{
    private const string EntryPointName = "vkGetInstanceProcAddr";

    /// <inheritdoc/>
    public bool IsMacOS => OperatingSystem.IsMacOS();

    /// <inheritdoc/>
    public bool FileExists(string path) => File.Exists(path);

    /// <inheritdoc/>
    public string? GetEnvironmentVariable(string name) => Environment.GetEnvironmentVariable(name);

    /// <inheritdoc/>
    /// <remarks>
    /// macOS only (unverified, no CI): Windows and Linux never set a driver variable. .NET keeps its own copy of the
    /// environment on Unix, so <see cref="Environment.SetEnvironmentVariable(string, string)"/> alone is invisible to
    /// the loader's <c>getenv</c>; the C runtime's copy is set first, through <c>setenv</c> from libSystem. Allocates the
    /// UTF-8 arguments and, on failure, the message.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null or empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
    /// <exception cref="PlatformNotSupportedException">The OS isn't macOS.</exception>
    public Result SetEnvironmentVariable(string name, string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(value);
        if (!OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException("The Vulkan loader setup sets environment variables on macOS only.");
        }

        if (SetEnv(name, value, overwrite: 1) != 0)
        {
            int errno = Marshal.GetLastPInvokeError();
            return new EngineError(ErrorKind.Native, "setenv(" + name + ") failed: " + Marshal.GetPInvokeErrorMessage(errno), errno);
        }

        Environment.SetEnvironmentVariable(name, value);
        return Result.Success;
    }

    /// <inheritdoc/>
    /// <remarks>Allocates the failure messages only.</remarks>
    /// <exception cref="ArgumentException"><paramref name="path"/> is null or not fully qualified.</exception>
    public Result<nint> Preload(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("A preloaded Vulkan loader must be a fully qualified path.", nameof(path));
        }

        if (!NativeLibrary.TryLoad(path, out nint library))
        {
            return new EngineError(ErrorKind.Unavailable, "Vulkan loader missing: could not load " + path);
        }

        if (!NativeLibrary.TryGetExport(library, EntryPointName, out nint entryPoint))
        {
            // Not a loader, so nothing will bind it: drop this load's reference.
            NativeLibrary.Free(library);
            return new EngineError(ErrorKind.Unavailable, "Not a Vulkan loader (no vkGetInstanceProcAddr export): " + path);
        }

        return entryPoint;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// BRUTAL resolves its loader in the static constructor of <c>Vulkan</c> (Brutal.Vulkan/ImportResolver.cs:16-37),
    /// which throws <see cref="DllNotFoundException"/> when nothing loads (:69); that surfaces as a
    /// <see cref="TypeInitializationException"/> on this and every later touch, the one exception caught here.
    /// Allocates nothing after the first call.
    /// </remarks>
    public unsafe nint BrutalGetInstanceProcAddr()
    {
        try
        {
            // Sound: only the function pointer's address is read; nothing is called through it.
            return (nint)global::Brutal.VulkanApi.Vulkan.GetInstanceProcAddr.Invoke;
        }
        catch (TypeInitializationException)
        {
            return 0;
        }
    }

    // libSystem holds the C runtime on macOS. An absolute path, so neither the JIT nor NativeAOT has to probe for it.
    [LibraryImport("/usr/lib/libSystem.B.dylib", EntryPoint = "setenv", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int SetEnv(string name, string value, int overwrite);
}
