namespace BrutalEngine.Graphics;

/// <summary>How a sampler addresses coordinates outside [0, 1].</summary>
/// <remarks>
/// docs/design/graphics.md section 5. Backends map each value to their native one by name, never by casting.
/// </remarks>
public enum AddressMode
{
    /// <summary>Clamps to the edge texels. The default value.</summary>
    ClampToEdge = 0,

    /// <summary>Wraps around, tiling the texture.</summary>
    Repeat = 1,

    /// <summary>Wraps around, mirroring the texture on every other repetition.</summary>
    MirrorRepeat = 2,

    /// <summary>Returns the sampler's border color.</summary>
    ClampToBorder = 3,
}
