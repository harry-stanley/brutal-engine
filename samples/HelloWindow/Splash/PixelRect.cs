namespace HelloWindow.Splash;

/// <summary>A rectangle in whole pixels, from the top-left corner.</summary>
/// <param name="X">Left edge in pixels.</param>
/// <param name="Y">Top edge in pixels.</param>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels.</param>
internal readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    /// <summary>Gets the rectangle's size.</summary>
    public PixelSize Size => new(Width, Height);

    /// <inheritdoc/>
    public override string ToString() => $"{Width}x{Height} at ({X}, {Y})";
}
