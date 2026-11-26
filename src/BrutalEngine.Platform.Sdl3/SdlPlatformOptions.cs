namespace BrutalEngine.Platform.Sdl3;

/// <summary>Settings specific to the SDL 3 platform backend, alongside the backend-neutral <see cref="PlatformOptions"/>.</summary>
/// <remarks>
/// The record only describes the settings; the backend validates them when it starts. A
/// <see cref="VulkanLibraryPath"/> that is set and not fully qualified throws <see cref="System.ArgumentException"/>.
/// Equality compares every property. An immutable record: safe to share between threads. Creating one allocates;
/// reading it doesn't.
/// </remarks>
public sealed record SdlPlatformOptions
{
    /// <summary>
    /// The fully qualified path of the Vulkan loader SDL loads for Vulkan windows, or <see langword="null"/> (the
    /// default) for SDL's own search.
    /// </summary>
    /// <remarks>
    /// Sets SDL's <c>SDL_VULKAN_LIBRARY</c> hint before SDL starts, so SDL and the graphics backend load one library;
    /// the host passes the loader the graphics backend chose. An <c>SDL_VULKAN_LIBRARY</c> environment variable
    /// overrides it: SDL refuses the hint, and the platform logs that and continues.
    /// </remarks>
    public string? VulkanLibraryPath { get; init; }
}
