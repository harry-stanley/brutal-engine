namespace BrutalEngine.Graphics;

/// <summary>How a color target blends a fragment's output with its existing value: one equation for color, one for alpha.</summary>
/// <param name="Color">The equation for the red, green and blue channels.</param>
/// <param name="Alpha">The equation for the alpha channel.</param>
/// <remarks>
/// The presets are the usual ones, with WebGPU's values (docs/design/graphics.md section 7); each property documents its
/// factors, and every equation adds. A <see cref="ColorTargetState"/> with no blend state writes the output unblended.
/// A plain unmanaged value. Equality compares every field. Immutable and safe to use from any thread. No member
/// allocates, except <see cref="object.ToString"/>.
/// </remarks>
public readonly record struct BlendState(BlendComponent Color, BlendComponent Alpha)
{
    /// <summary>
    /// Straight (non-premultiplied) alpha blending: color <c>(SourceAlpha, OneMinusSourceAlpha)</c>, alpha
    /// <c>(One, OneMinusSourceAlpha)</c>, both with <see cref="BlendOperation.Add"/>.
    /// </summary>
    public static BlendState AlphaBlending => new(
        new(BlendFactor.SourceAlpha, BlendFactor.OneMinusSourceAlpha),
        new(BlendFactor.One, BlendFactor.OneMinusSourceAlpha));

    /// <summary>
    /// Blending for colors already multiplied by their alpha: <c>(One, OneMinusSourceAlpha)</c> for both color and alpha,
    /// with <see cref="BlendOperation.Add"/>.
    /// </summary>
    public static BlendState PremultipliedAlpha => new(
        new(BlendFactor.One, BlendFactor.OneMinusSourceAlpha),
        new(BlendFactor.One, BlendFactor.OneMinusSourceAlpha));

    /// <summary>
    /// Additive blending weighted by the source alpha: color <c>(SourceAlpha, One)</c>, alpha <c>(One, One)</c>, both
    /// with <see cref="BlendOperation.Add"/>.
    /// </summary>
    public static BlendState Additive => new(
        new(BlendFactor.SourceAlpha, BlendFactor.One),
        new(BlendFactor.One, BlendFactor.One));
}
