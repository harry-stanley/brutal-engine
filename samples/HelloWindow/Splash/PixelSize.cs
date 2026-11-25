namespace HelloWindow.Splash;

/// <summary>A size in whole pixels.</summary>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels.</param>
internal readonly record struct PixelSize(int Width, int Height)
{
    /// <inheritdoc/>
    public override string ToString() => $"{Width}x{Height}";
}
