namespace BrutalEngine.Graphics;

/// <summary>How a graphics pipeline rasterizes triangles: which faces it culls and which winding is the front.</summary>
/// <param name="CullMode">The faces discarded; <see cref="CullMode.None"/> by default.</param>
/// <param name="FrontFace">The winding that faces the front; <see cref="FrontFace.CounterClockwise"/> by default.</param>
/// <remarks>
/// Part of <see cref="GraphicsPipelineDescription"/> (docs/design/graphics.md section 7). <c>default(RasterState)</c>
/// equals <c>new RasterState()</c>, so the defaults hold either way. A plain unmanaged value. Equality compares every field.
/// Immutable and safe to use from any thread. No member allocates, except <see cref="object.ToString"/>.
/// </remarks>
public readonly record struct RasterState(CullMode CullMode = CullMode.None, FrontFace FrontFace = FrontFace.CounterClockwise);
