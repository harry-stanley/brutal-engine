using System.Globalization;

namespace BrutalEngine.Graphics;

/// <summary>Describes a view of a texture: which mip levels and array layers shaders and passes see, and how.</summary>
/// <remarks>
/// <para>
/// The device validates a description against its texture before the backend sees it (docs/design/graphics.md
/// section 5). A view outside its texture (a negative base, a zero count, or a range past the texture's mip levels
/// or layers) or a <see cref="Dimension"/> outside the enum throws <see cref="System.ArgumentOutOfRangeException"/>; a
/// <see cref="TextureViewDimension.Texture2D"/> view of more than one layer throws
/// <see cref="System.ArgumentException"/>. A stale or null <see cref="Texture"/> is the device's handle check.
/// </para>
/// <para>
/// Created with <c>new TextureViewDescription { ... }</c>, a description views one mip level and one layer from the
/// first; <c>default(TextureViewDescription)</c> has zero counts, and is invalid. An immutable value: safe to copy and
/// to use from any thread. Equality compares every property; the debug name compares ordinally. The device keeps no
/// reference to the description or its <see cref="DebugName"/>. No member allocates, except <see cref="ToString"/>.
/// </para>
/// </remarks>
public readonly record struct TextureViewDescription
{
    /// <summary>
    /// Creates a description that views one mip level and one layer from the first. Use an object initializer, which
    /// must set the required properties.
    /// </summary>
    public TextureViewDescription()
    {
    }

    /// <summary>The texture to view. The view doesn't keep it alive: destroying the texture makes the view stale.</summary>
    public required TextureHandle Texture { get; init; }

    /// <summary>How shaders see the view. Defaults to <see cref="TextureViewDimension.Texture2D"/>.</summary>
    public TextureViewDimension Dimension { get; init; }

    /// <summary>The first mip level the view sees: from 0 to the texture's mip levels minus 1. Defaults to 0.</summary>
    public int BaseMipLevel { get; init; }

    /// <summary>
    /// The number of mip levels the view sees: at least 1, at most the texture's mip levels minus
    /// <see cref="BaseMipLevel"/>. Defaults to 1.
    /// </summary>
    public int MipLevelCount { get; init; } = 1;

    /// <summary>The first array layer the view sees: from 0 to the texture's layers minus 1. Defaults to 0.</summary>
    public int BaseArrayLayer { get; init; }

    /// <summary>
    /// The number of array layers the view sees: at least 1, at most the texture's layers minus
    /// <see cref="BaseArrayLayer"/>, and exactly 1 for a <see cref="TextureViewDimension.Texture2D"/> view. Defaults to 1.
    /// </summary>
    public int ArrayLayerCount { get; init; } = 1;

    /// <summary>
    /// An optional name for debuggers, validation messages, errors and the leak report (docs/design/graphics.md
    /// section 13); <see langword="null"/> for none.
    /// </summary>
    public string? DebugName { get; init; }

    /// <summary>
    /// Returns every property, for example
    /// <c>TextureViewDescription { Texture = Texture(3:1), Dimension = Texture2DArray, BaseMipLevel = 0, MipLevelCount = 1, BaseArrayLayer = 2, ArrayLayerCount = 4, DebugName = layers }</c>.
    /// </summary>
    /// <returns>A diagnostic string, formatted with the invariant culture whatever the current culture is. Allocates.</returns>
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"TextureViewDescription {{ Texture = {Texture}, Dimension = {Dimension}, BaseMipLevel = {BaseMipLevel}, MipLevelCount = {MipLevelCount}, BaseArrayLayer = {BaseArrayLayer}, ArrayLayerCount = {ArrayLayerCount}, DebugName = {DebugName} }}");
}
