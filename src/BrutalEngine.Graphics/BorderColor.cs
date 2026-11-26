namespace BrutalEngine.Graphics;

/// <summary>The color a sampler returns outside the texture with <see cref="AddressMode.ClampToBorder"/>.</summary>
/// <remarks>
/// docs/design/graphics.md section 5. Backends map each value to their native one by name, never by casting.
/// </remarks>
public enum BorderColor
{
    /// <summary>Black with zero alpha: (0, 0, 0, 0). The default value.</summary>
    TransparentBlack = 0,

    /// <summary>Black with full alpha: (0, 0, 0, 1).</summary>
    OpaqueBlack = 1,

    /// <summary>White with full alpha: (1, 1, 1, 1).</summary>
    OpaqueWhite = 2,
}
