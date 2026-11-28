namespace BrutalEngine.Graphics;

/// <summary>One color target a graphics pipeline renders to: its format, blending and the channels written.</summary>
/// <param name="Format">The target's format; a render pass's color attachment at the same index must have it.</param>
/// <param name="Blend">How the output blends with the target, or <see langword="null"/> to write it unblended.</param>
/// <param name="WriteMask">
/// The channels written; <see cref="ColorWriteMask.All"/> by default with the positional constructor only:
/// <c>default(ColorTargetState)</c> and an object initializer without it give <see cref="ColorWriteMask.None"/>, which writes nothing.
/// </param>
/// <remarks>
/// Render target formats are part of the pipeline, because dynamic rendering has no render pass objects
/// (docs/design/graphics.md section 7): a pass whose attachments don't match the bound pipeline's formats throws
/// <see cref="System.InvalidOperationException"/> when the pipeline is set. When the pipeline is created, a
/// <paramref name="Format"/> that isn't a color format (<see cref="TextureFormat.Undefined"/> or a depth format) throws
/// <see cref="System.ArgumentException"/>, and a value outside its enum (the format, a blend factor or operation, the
/// write mask) <see cref="System.ArgumentOutOfRangeException"/>; whether the device supports the format is the device's
/// check. A plain unmanaged value. Equality compares every field. Immutable and safe to use from any thread. No member
/// allocates, except <see cref="object.ToString"/>.
/// </remarks>
public readonly record struct ColorTargetState(TextureFormat Format, BlendState? Blend, ColorWriteMask WriteMask = ColorWriteMask.All);
