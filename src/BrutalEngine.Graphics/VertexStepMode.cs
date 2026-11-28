namespace BrutalEngine.Graphics;

/// <summary>How often a vertex buffer advances to its next element.</summary>
/// <remarks>
/// docs/design/graphics.md section 7.
/// </remarks>
public enum VertexStepMode
{
    /// <summary>Once per vertex. The default value.</summary>
    Vertex = 0,

    /// <summary>Once per instance.</summary>
    Instance = 1,
}
