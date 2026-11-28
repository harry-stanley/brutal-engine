namespace BrutalEngine.Graphics;

/// <summary>How a blend equation combines the weighted source and destination values.</summary>
/// <remarks>
/// With source <c>s</c>, destination <c>d</c> and their factors <c>fs</c> and <c>fd</c> (docs/design/graphics.md section 7).
/// <see cref="Min"/> and <see cref="Max"/> ignore the factors, as in Vulkan and WebGPU.
/// </remarks>
public enum BlendOperation
{
    /// <summary><c>s * fs + d * fd</c>. The default value.</summary>
    Add = 0,

    /// <summary><c>s * fs - d * fd</c>.</summary>
    Subtract = 1,

    /// <summary><c>d * fd - s * fs</c>.</summary>
    ReverseSubtract = 2,

    /// <summary><c>min(s, d)</c>.</summary>
    Min = 3,

    /// <summary><c>max(s, d)</c>.</summary>
    Max = 4,
}
