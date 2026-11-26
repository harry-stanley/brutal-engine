namespace BrutalEngine.Platform;

/// <summary>A width and height. The member that returns or takes it says which unit it is in.</summary>
/// <param name="Width">The width.</param>
/// <param name="Height">The height.</param>
/// <remarks>
/// <para>
/// Window sizes come in three units (see docs/design/platform.md, "Window coordinates, pixels and content units"):
/// window coordinates (<c>IWindow.Size</c>), pixels (<c>IWindow.PixelSize</c>) and content units
/// (<c>IWindow.ContentSize</c>). The type doesn't record which one a value is in.
/// </para>
/// <para>
/// <c>default(WindowSize)</c> is 0 by 0, which is what a minimised window can report as its pixel size.
/// Equality compares both sides. Immutable and safe to use from any thread. No member allocates, except
/// <c>ToString</c>.
/// </para>
/// </remarks>
public readonly record struct WindowSize(int Width, int Height);
