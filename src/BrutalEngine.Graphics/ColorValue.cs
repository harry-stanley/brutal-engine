using System.Globalization;

namespace BrutalEngine.Graphics;

/// <summary>A color in linear space, one 32-bit float per channel: a color attachment's clear value (docs/design/graphics.md section 9).</summary>
/// <param name="R">The red channel.</param>
/// <param name="G">The green channel.</param>
/// <param name="B">The blue channel.</param>
/// <param name="A">The alpha channel.</param>
/// <remarks>
/// <para>
/// The channels are linear: an sRGB attachment encodes them when it is cleared, and a linear (<c>Unorm</c>) attachment
/// stores them as given (as <see cref="SwapchainFormat.Unorm"/> does). <c>default</c> is transparent black. The type
/// itself accepts any values.
/// </para>
/// <para>
/// A plain unmanaged value, so the command stream holds it as raw bytes. Equality compares every channel
/// (floating-point equality, so a <see cref="float.NaN"/> channel never compares equal). Immutable and safe to use from
/// any thread. No member allocates, except <see cref="ToString"/>.
/// </para>
/// </remarks>
public readonly record struct ColorValue(float R, float G, float B, float A)
{
    /// <summary>Returns every channel in the record's usual form, for example <c>ColorValue { R = 0.5, G = 0, B = 1, A = 1 }</c>.</summary>
    /// <returns>
    /// A diagnostic string, formatted with the invariant culture whatever the current culture is: an ASCII minus sign, a
    /// period as the decimal separator, and <c>NaN</c> and <c>Infinity</c> for those values. Allocates.
    /// </returns>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"ColorValue {{ R = {R}, G = {G}, B = {B}, A = {A} }}");
}
