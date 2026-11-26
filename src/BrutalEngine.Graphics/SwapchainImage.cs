using System.Globalization;

namespace BrutalEngine.Graphics;

/// <summary>The swapchain image acquired for the frame being recorded: its texture, view, size and format.</summary>
/// <param name="Texture">The image's texture. Owned by the swapchain: never destroy it.</param>
/// <param name="View">The image's view, to render to as a color target or, with copy support, to copy into.</param>
/// <param name="Width">The image's width in pixels.</param>
/// <param name="Height">The image's height in pixels.</param>
/// <param name="Format">The image's format.</param>
/// <remarks>
/// <para>
/// Valid only in the frame that acquired it (docs/design/graphics.md section 3). The view may be a color target or, if
/// <see cref="SwapchainInfo.SupportsCopyDestination"/>, a copy destination. The device presents the image when the frame
/// is submitted, and clears it to black first if no pass wrote it. An acquire that returns no image leaves
/// <c>default(SwapchainImage)</c>: null handles, zero size and <see cref="TextureFormat.Undefined"/>.
/// </para>
/// <para>
/// The value doesn't keep the image alive. Equality compares every field. Immutable and safe to use from any thread,
/// though the device accepts its handles only on its owner thread. No member allocates, except <see cref="ToString"/>.
/// </para>
/// </remarks>
public readonly record struct SwapchainImage(TextureHandle Texture, TextureViewHandle View, int Width, int Height, TextureFormat Format)
{
    /// <summary>
    /// Returns every field in the record's usual form, for example
    /// <c>SwapchainImage { Texture = Texture(4:2), View = TextureView(9:2), Width = 1920, Height = 1080, Format = Bgra8UnormSrgb }</c>.
    /// </summary>
    /// <returns>A diagnostic string, formatted with the invariant culture whatever the current culture is. Allocates.</returns>
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"SwapchainImage {{ Texture = {Texture}, View = {View}, Width = {Width}, Height = {Height}, Format = {Format} }}");
}
