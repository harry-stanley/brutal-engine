namespace BrutalEngine.Graphics;

/// <summary>The winding order that makes a triangle front-facing.</summary>
/// <remarks>
/// Winding is judged as the triangle appears on the render target, with clip space's y axis pointing up: the
/// convention every backend matches (docs/design/graphics.md section 7), so a winding means the same on every backend.
/// </remarks>
public enum FrontFace
{
    /// <summary>Counter-clockwise triangles face the front. The default value.</summary>
    CounterClockwise = 0,

    /// <summary>Clockwise triangles face the front.</summary>
    Clockwise = 1,
}
