using System;
using System.Runtime.InteropServices;
using Brutal.VulkanApi;

namespace BrutalEngine.Aot.Smoke;

/// <summary>Makes BRUTAL use a Vulkan loader at a known absolute path, and proves that it did.</summary>
/// <remarks>
/// <para>
/// BRUTAL's resolver (Brutal.Vulkan/ImportResolver.cs) loads the loader by bare name in the static constructor of
/// <see cref="Vulkan"/>. On macOS it never probes the app directory, and no system loader exists without the Vulkan
/// SDK or Homebrew. Loading the bundled loader by absolute path first works because the bare-name dlopen of
/// <c>libvulkan.1.dylib</c> then binds to the image that is already in the process (spike S1,
/// docs/spikes/README.md). This mirrors the engine duty in docs/02-tech-stack.md.
/// </para>
/// <para>
/// <see cref="Preload"/> must run before any member of <see cref="Vulkan"/> is touched. <see cref="IsUsedByBrutal"/>
/// then compares BRUTAL's <c>vkGetInstanceProcAddr</c> with the one exported by the preloaded library, so a run that
/// silently fell back to another loader (for example a system copy) fails instead of passing.
/// </para>
/// </remarks>
internal static unsafe class VulkanLoader
{
    private const string EntryPoint = "vkGetInstanceProcAddr";

    /// <summary>Loads the Vulkan loader at <paramref name="path"/> and keeps it loaded for the process lifetime.</summary>
    /// <param name="path">Absolute path of the loader library.</param>
    /// <returns>The address of the library's <c>vkGetInstanceProcAddr</c>.</returns>
    /// <exception cref="DllNotFoundException">The library could not be loaded.</exception>
    /// <exception cref="EntryPointNotFoundException">The library is not a Vulkan loader.</exception>
    public static nint Preload(string path)
    {
        nint library = NativeLibrary.Load(path);
        return NativeLibrary.GetExport(library, EntryPoint);
    }

    /// <summary>Checks that BRUTAL resolved Vulkan to the library returned by <see cref="Preload"/>.</summary>
    /// <param name="preloadedEntryPoint">The value returned by <see cref="Preload"/>.</param>
    /// <returns><see langword="true"/> if BRUTAL's <c>vkGetInstanceProcAddr</c> is the preloaded one.</returns>
    /// <remarks>Touching <see cref="Vulkan.GetInstanceProcAddr"/> runs BRUTAL's loader resolution if it hasn't run yet.</remarks>
    public static bool IsUsedByBrutal(nint preloadedEntryPoint) => (nint)Vulkan.GetInstanceProcAddr.Invoke == preloadedEntryPoint;
}
