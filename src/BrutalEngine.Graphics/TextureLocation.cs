using System.Globalization;

namespace BrutalEngine.Graphics;

/// <summary>One subresource of a texture, and the aspect of it, that a copy or a write reads or writes.</summary>
/// <param name="Texture">The texture.</param>
/// <param name="MipLevel">The mip level: from 0 to the texture's mip levels minus 1. Defaults to 0.</param>
/// <param name="ArrayLayer">The array layer: from 0 to the texture's layers minus 1. Defaults to 0. A copy covers one layer.</param>
/// <param name="Aspect">
/// The aspect: <see cref="TextureAspect.Color"/> for color formats, <see cref="TextureAspect.Depth"/> for depth
/// formats. Defaults to <see cref="TextureAspect.Color"/>.
/// </param>
/// <remarks>
/// <para>
/// The device checks a location against its texture before it records the copy or write that uses it
/// (docs/design/graphics.md section 6). A mip level or layer outside the texture, or an aspect outside the enum, throws
/// <see cref="System.ArgumentOutOfRangeException"/>; an aspect that doesn't match the format throws
/// <see cref="System.ArgumentException"/>, and so does a texture with more than one sample: every copy and write needs
/// one. A stale or null <see cref="Texture"/> is the device's handle check.
/// <c>default(TextureLocation)</c> has the null texture, mip 0, layer 0 and the color aspect.
/// </para>
/// <para>
/// The location doesn't keep the texture alive. Equality compares every field. Immutable and safe to use from any
/// thread. No member allocates, except <see cref="ToString"/>.
/// </para>
/// </remarks>
public readonly record struct TextureLocation(TextureHandle Texture, int MipLevel = 0, int ArrayLayer = 0, TextureAspect Aspect = TextureAspect.Color)
{
    /// <summary>
    /// Returns every field in the record's usual form, for example
    /// <c>TextureLocation { Texture = Texture(3:1), MipLevel = 0, ArrayLayer = 2, Aspect = Color }</c>.
    /// </summary>
    /// <returns>A diagnostic string, formatted with the invariant culture whatever the current culture is. Allocates.</returns>
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"TextureLocation {{ Texture = {Texture}, MipLevel = {MipLevel}, ArrayLayer = {ArrayLayer}, Aspect = {Aspect} }}");
}
