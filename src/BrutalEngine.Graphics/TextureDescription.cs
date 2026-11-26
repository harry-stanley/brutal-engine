using System.Globalization;

namespace BrutalEngine.Graphics;

/// <summary>Describes a 2D texture (or 2D array texture) to create: its format, size, layers, mips, samples and usage.</summary>
/// <remarks>
/// <para>
/// The device validates a description before the backend sees it (docs/design/graphics.md section 5). A structurally
/// invalid description throws <see cref="System.ArgumentException"/> or <see cref="System.ArgumentOutOfRangeException"/>:
/// <see cref="TextureFormat.Undefined"/>, a non-positive side, layer or mip count, more mips than
/// <c>floor(log2(max(Width, Height))) + 1</c>, a sample count that isn't a power of two from 1 to 16, multisampling with
/// several mips or layers, no usage, or a format or usage outside the enum's members. A side above <see cref="GraphicsLimits.MaxTextureDimension2D"/> or more layers
/// than <see cref="GraphicsLimits.MaxTextureArrayLayers"/> throws <see cref="System.ArgumentOutOfRangeException"/>. A
/// format, usage or sample count the device doesn't support is a <c>Result</c> failure, because it depends on the
/// hardware.
/// </para>
/// <para>
/// Created with <c>new TextureDescription { ... }</c>, a description has one layer, one mip level and one sample;
/// <c>default(TextureDescription)</c> has zero of each, and is invalid. An immutable value: safe to copy and to use
/// from any thread. Equality compares every property; the debug name compares ordinally. The device copies what it
/// needs and keeps no reference to the description or its <see cref="DebugName"/>. No member allocates, except
/// <see cref="ToString"/>.
/// </para>
/// </remarks>
public readonly record struct TextureDescription
{
    /// <summary>
    /// Creates a description with one layer, one mip level and one sample. Use an object initializer, which must set
    /// the required properties.
    /// </summary>
    public TextureDescription()
    {
    }

    /// <summary>The texel format; never <see cref="TextureFormat.Undefined"/>.</summary>
    public required TextureFormat Format { get; init; }

    /// <summary>The width in texels: at least 1, at most <see cref="GraphicsLimits.MaxTextureDimension2D"/>.</summary>
    public required int Width { get; init; }

    /// <summary>The height in texels: at least 1, at most <see cref="GraphicsLimits.MaxTextureDimension2D"/>.</summary>
    public required int Height { get; init; }

    /// <summary>
    /// The number of array layers: at least 1, at most <see cref="GraphicsLimits.MaxTextureArrayLayers"/>. Defaults to 1.
    /// </summary>
    public int ArrayLayers { get; init; } = 1;

    /// <summary>
    /// The number of mip levels: at least 1, at most <c>floor(log2(max(Width, Height))) + 1</c>. Defaults to 1. M1
    /// content uses one level; the API supports more.
    /// </summary>
    public int MipLevels { get; init; } = 1;

    /// <summary>
    /// The samples per texel: 1, 2, 4, 8 or 16. Defaults to 1. More than one needs a single mip level, a single layer,
    /// and a format whose support includes <see cref="FormatSupport.Multisample"/>.
    /// </summary>
    public int SampleCount { get; init; } = 1;

    /// <summary>How the texture will be used; at least one flag.</summary>
    public required TextureUsage Usage { get; init; }

    /// <summary>
    /// An optional name for debuggers, validation messages, errors and the leak report (docs/design/graphics.md
    /// section 13); <see langword="null"/> for none.
    /// </summary>
    public string? DebugName { get; init; }

    /// <summary>
    /// Returns every property, for example
    /// <c>TextureDescription { Format = Rgba8Unorm, Width = 256, Height = 128, ArrayLayers = 1, MipLevels = 1, SampleCount = 1, Usage = Sampled, CopyDestination, DebugName = atlas }</c>.
    /// </summary>
    /// <returns>A diagnostic string, formatted with the invariant culture whatever the current culture is. Allocates.</returns>
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"TextureDescription {{ Format = {Format}, Width = {Width}, Height = {Height}, ArrayLayers = {ArrayLayers}, MipLevels = {MipLevels}, SampleCount = {SampleCount}, Usage = {Usage}, DebugName = {DebugName} }}");
}
