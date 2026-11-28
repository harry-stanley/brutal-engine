namespace BrutalEngine.Graphics;

/// <summary>Which triangles the rasterizer discards, by the side they face.</summary>
/// <remarks>
/// Which side is the front is <see cref="RasterState.FrontFace"/> (docs/design/graphics.md section 7).
/// </remarks>
public enum CullMode
{
    /// <summary>No triangles are culled. The default value.</summary>
    None = 0,

    /// <summary>Front-facing triangles are culled.</summary>
    Front = 1,

    /// <summary>Back-facing triangles are culled.</summary>
    Back = 2,
}
