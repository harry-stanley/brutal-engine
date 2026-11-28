namespace BrutalEngine.Graphics;

/// <summary>How a draw's vertices are assembled into primitives.</summary>
/// <remarks>
/// Strip topologies never restart within a draw in this version (docs/design/graphics.md section 7).
/// </remarks>
public enum PrimitiveTopology
{
    /// <summary>Every three vertices form a triangle. The default value.</summary>
    TriangleList = 0,

    /// <summary>Each vertex after the second forms a triangle with the two before it.</summary>
    TriangleStrip = 1,

    /// <summary>Every two vertices form a line.</summary>
    LineList = 2,

    /// <summary>Each vertex after the first forms a line with the one before it.</summary>
    LineStrip = 3,
}
