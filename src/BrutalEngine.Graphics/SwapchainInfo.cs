using System.Globalization;

namespace BrutalEngine.Graphics;

/// <summary>What a swapchain really is after creation or recreation: its size, format, present mode and image count.</summary>
/// <param name="Width">The images' width in pixels.</param>
/// <param name="Height">The images' height in pixels.</param>
/// <param name="Format">The images' format, of the <see cref="SwapchainFormat"/> kind that was asked for.</param>
/// <param name="PresentMode">
/// The present mode in use: the requested one, or <see cref="Graphics.PresentMode.Fifo"/> if the surface doesn't offer it.
/// </param>
/// <param name="ImageCount">The number of images in the swapchain.</param>
/// <param name="SupportsCopyDestination">Whether an acquired image can be a copy destination as well as a color target.</param>
/// <remarks>
/// <para>
/// The device reports it (docs/design/graphics.md section 3); it changes only when the swapchain is recreated, which an
/// acquire reports as <see cref="AcquireStatus.Recreated"/>. <c>default(SwapchainInfo)</c> is all zeros, with
/// <see cref="TextureFormat.Undefined"/>, and describes no swapchain.
/// </para>
/// <para>
/// Equality compares every field. Immutable and safe to use from any thread. No member allocates, except
/// <see cref="ToString"/>.
/// </para>
/// </remarks>
public readonly record struct SwapchainInfo(
    int Width, int Height, TextureFormat Format, PresentMode PresentMode, int ImageCount, bool SupportsCopyDestination)
{
    /// <summary>
    /// Returns every field in the record's usual form, for example
    /// <c>SwapchainInfo { Width = 1920, Height = 1080, Format = Bgra8UnormSrgb, PresentMode = Fifo, ImageCount = 3, SupportsCopyDestination = True }</c>.
    /// </summary>
    /// <returns>A diagnostic string, formatted with the invariant culture whatever the current culture is. Allocates.</returns>
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"SwapchainInfo {{ Width = {Width}, Height = {Height}, Format = {Format}, PresentMode = {PresentMode}, ImageCount = {ImageCount}, SupportsCopyDestination = {SupportsCopyDestination} }}");
}
