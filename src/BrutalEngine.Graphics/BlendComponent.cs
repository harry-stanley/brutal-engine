namespace BrutalEngine.Graphics;

/// <summary>One blend equation: the color channels' or the alpha channel's.</summary>
/// <param name="Source">The factor the fragment shader's output is multiplied by.</param>
/// <param name="Destination">The factor the color target's existing value is multiplied by.</param>
/// <param name="Operation">How the weighted values combine; <see cref="BlendOperation.Add"/> by default.</param>
/// <remarks>
/// The result written is <c>Operation(source * Source, destination * Destination)</c> (docs/design/graphics.md section 7).
/// A plain unmanaged value. Equality compares every field. Immutable and safe to use from any thread. No member
/// allocates, except <see cref="object.ToString"/>.
/// </remarks>
public readonly record struct BlendComponent(BlendFactor Source, BlendFactor Destination, BlendOperation Operation = BlendOperation.Add);
