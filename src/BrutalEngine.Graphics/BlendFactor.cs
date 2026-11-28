namespace BrutalEngine.Graphics;

/// <summary>A factor a blend equation multiplies a source or destination value by.</summary>
/// <remarks>
/// The source is the fragment shader's output and the destination the value already in the color target
/// (docs/design/graphics.md section 7). Each factor applies per channel; the alpha factors take the alpha channel.
/// </remarks>
public enum BlendFactor
{
    /// <summary>0.</summary>
    Zero = 0,

    /// <summary>1.</summary>
    One = 1,

    /// <summary>The source color.</summary>
    SourceColor = 2,

    /// <summary>1 minus the source color.</summary>
    OneMinusSourceColor = 3,

    /// <summary>The source alpha.</summary>
    SourceAlpha = 4,

    /// <summary>1 minus the source alpha.</summary>
    OneMinusSourceAlpha = 5,

    /// <summary>The destination color.</summary>
    DestinationColor = 6,

    /// <summary>1 minus the destination color.</summary>
    OneMinusDestinationColor = 7,

    /// <summary>The destination alpha.</summary>
    DestinationAlpha = 8,

    /// <summary>1 minus the destination alpha.</summary>
    OneMinusDestinationAlpha = 9,
}
