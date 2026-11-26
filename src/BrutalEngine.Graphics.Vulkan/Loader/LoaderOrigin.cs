namespace BrutalEngine.Graphics.Vulkan.Loader;

/// <summary>Where the Vulkan loader that BRUTAL bound came from (docs/design/graphics-vulkan.md section 2).</summary>
internal enum LoaderOrigin
{
    /// <summary>BRUTAL's own search: <c>vulkan-1</c> from System32 on Windows, the distro's loader on Linux, and on macOS
    /// the installed loaders BRUTAL knows. Nothing was preloaded.</summary>
    System,

    /// <summary>The loader bundled next to the app (macOS only, unverified), preloaded by absolute path.</summary>
    Bundled,

    /// <summary>The loader the host named explicitly, preloaded by absolute path on any OS.</summary>
    Explicit,
}
