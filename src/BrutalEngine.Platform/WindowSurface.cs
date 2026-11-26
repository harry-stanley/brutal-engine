namespace BrutalEngine.Platform;

/// <summary>The graphics surface a window is created for (<see cref="WindowOptions.Surface"/>).</summary>
/// <remarks>The values are fixed: new surfaces are appended, never renumbered.</remarks>
public enum WindowSurface
{
    /// <summary>
    /// No graphics surface: <see cref="IWindow.VulkanSurface"/> is <see langword="null"/>. For platform tests and
    /// servers, and the only choice on a <see cref="HeadlessMode.NoSurface"/> platform.
    /// </summary>
    None = 0,

    /// <summary>
    /// A window Vulkan can present to, with an <see cref="IWindow.VulkanSurface"/>. The default in
    /// <see cref="WindowOptions"/>.
    /// </summary>
    Vulkan = 1,
}
