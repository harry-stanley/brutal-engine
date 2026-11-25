namespace HelloWindow.Splash;

/// <summary>The byte order of a 32-bit pixel in memory, matching the image format the logo is uploaded as.</summary>
internal enum LogoPixelOrder
{
    /// <summary>Red, green, blue, alpha: <c>R8G8B8A8_*</c> formats.</summary>
    Rgba = 0,

    /// <summary>Blue, green, red, alpha: <c>B8G8R8A8_*</c> formats.</summary>
    Bgra,
}
