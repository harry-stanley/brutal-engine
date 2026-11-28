namespace BrutalEngine.Graphics;

/// <summary>A graphics pipeline's depth target: its format, whether depth is written, and the depth test.</summary>
/// <param name="Format">The depth target's format; a render pass's depth attachment must have it.</param>
/// <param name="Write">Whether passing fragments write their depth.</param>
/// <param name="Compare">
/// The depth test: a fragment passes when <c>Compare(fragment depth, stored depth)</c> holds;
/// <see cref="CompareFunction.LessOrEqual"/> by default.
/// </param>
/// <remarks>
/// There is no stencil state in M1 (docs/design/graphics.md section 7). Clip-space depth runs from 0 to 1 on every
/// backend. A plain unmanaged value. Equality compares every field. Immutable and safe to use from any thread. No member
/// allocates, except <see cref="object.ToString"/>.
/// </remarks>
public readonly record struct DepthState(TextureFormat Format, bool Write, CompareFunction Compare = CompareFunction.LessOrEqual);
