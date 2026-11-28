namespace BrutalEngine.Graphics;

/// <summary>One color target a graphics pipeline renders to: its format, blending and the channels written.</summary>
/// <param name="Format">The target's format; a render pass's color attachment at the same index must have it.</param>
/// <param name="Blend">How the output blends with the target, or <see langword="null"/> to write it unblended.</param>
/// <param name="WriteMask">The channels written; <see cref="ColorWriteMask.All"/> by default.</param>
/// <remarks>
/// Render target formats are part of the pipeline, because dynamic rendering has no render pass objects
/// (docs/design/graphics.md section 7): a pass whose attachments don't match the bound pipeline's formats throws
/// <see cref="System.InvalidOperationException"/> when the pipeline is set.
/// A plain unmanaged value. Equality compares every field. Immutable and safe to use from any thread. No member
/// allocates, except <see cref="object.ToString"/>.
/// </remarks>
public readonly record struct ColorTargetState(TextureFormat Format, BlendState? Blend, ColorWriteMask WriteMask = ColorWriteMask.All);
