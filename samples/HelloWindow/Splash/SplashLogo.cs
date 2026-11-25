using System;
using Brutal.SdlApi;

namespace HelloWindow.Splash;

/// <summary>The decoded BRUTAL logo: straight-alpha RGBA8 pixels in rows of exactly <c>Size.Width * 4</c> bytes.</summary>
/// <remarks>Decoded once at startup and kept for the run, so the GPU copy can be rebuilt if the swapchain format changes.
/// Immutable after <see cref="Load"/>.</remarks>
internal sealed class SplashLogo
{
    /// <summary>The folder next to the executable that HelloWindow.csproj copies the logo into.</summary>
    public const string AssetsFolder = "Assets";

    /// <summary>The logo's file name: BRUTAL's official logo for dark backgrounds.</summary>
    public const string FileName = "LOGO_BRUTAL_WHT.png";

    private SplashLogo(PixelSize size, byte[] rgba)
    {
        Size = size;
        Rgba = rgba;
    }

    /// <summary>Gets the logo's size in pixels.</summary>
    public PixelSize Size { get; }

    /// <summary>Gets the pixels: red, green, blue, alpha, sRGB-encoded with straight alpha, top row first.</summary>
    public ReadOnlyMemory<byte> Rgba { get; }

    /// <summary>Decodes a PNG with SDL 3.4's built-in decoder (<c>SDL_LoadPNG</c>, no extra dependency).</summary>
    /// <param name="path">The PNG's path.</param>
    /// <returns>The decoded logo.</returns>
    /// <exception cref="SdlException">SDL couldn't read, decode, convert or lock the image.</exception>
    /// <exception cref="SampleFailureException">The image is empty or too large.</exception>
    public static unsafe SplashLogo Load(string path)
    {
        // Brutal.Sdl/Generated/Sdl.PInvoke.gen.cs: LoadPNG (SDL_LoadPNG), ConvertSurface, LockSurface, UnlockSurface and
        // DestroySurface. Each returns null or false on failure, with the reason in SDL_GetError.
        SdlSurface* decoded = Sdl.PInvoke.LoadPNG(path);
        if (decoded == null)
        {
            throw SdlException.FromError($"SDL_LoadPNG({path}) failed");
        }

        SdlSurface* rgba;
        try
        {
            // RGBA32 is the byte order R, G, B, A in memory on every platform (SDL_PIXELFORMAT_RGBA32).
            rgba = Sdl.PInvoke.ConvertSurface(decoded, SdlPixelFormat.Rgba32);
        }
        finally
        {
            Sdl.PInvoke.DestroySurface(decoded);
        }

        if (rgba == null)
        {
            throw SdlException.FromError("SDL_ConvertSurface(SDL_PIXELFORMAT_RGBA32) failed");
        }

        try
        {
            return Copy(rgba);
        }
        finally
        {
            Sdl.PInvoke.DestroySurface(rgba);
        }
    }

    private static unsafe SplashLogo Copy(SdlSurface* surface)
    {
        // Far above the 712x168 logo; keeps width * height * 4 well inside an int.
        const int MaxDimension = 8192;
        int width = surface->w;
        int height = surface->h;
        if (width < 1 || height < 1 || width > MaxDimension || height > MaxDimension || surface->pitch < width * 4)
        {
            throw new SampleFailureException($"The splash logo is {width}x{height} (pitch {surface->pitch}); expected 1x1 to {MaxDimension}x{MaxDimension}.");
        }

        // SDL_MUSTLOCK: only RLE-encoded surfaces need locking, but checking costs nothing.
        bool mustLock = (surface->flags & SdlSurfaceFlags.LockNeeded) != 0;
        if (mustLock)
        {
            NativeCheck.Sdl(Sdl.PInvoke.LockSurface(surface) != 0, "SDL_LockSurface");
        }

        try
        {
            int rowBytes = width * 4;
            byte[] pixels = new byte[rowBytes * height];
            for (int y = 0; y < height; y++)
            {
                // Sound: the surface owns height rows of pitch >= rowBytes bytes (checked above), valid until it is
                // destroyed after this method returns, and SDL doesn't write to it meanwhile.
                ReadOnlySpan<byte> row = new((byte*)surface->pixels + ((long)y * surface->pitch), rowBytes);
                row.CopyTo(pixels.AsSpan(y * rowBytes, rowBytes));
            }

            return new SplashLogo(new PixelSize(width, height), pixels);
        }
        finally
        {
            if (mustLock)
            {
                Sdl.PInvoke.UnlockSurface(surface);
            }
        }
    }
}
