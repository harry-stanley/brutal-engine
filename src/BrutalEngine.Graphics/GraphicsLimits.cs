using System.Globalization;

namespace BrutalEngine.Graphics;

/// <summary>The adapter limits the engine reads from the backend and checks resource descriptions against.</summary>
/// <remarks>
/// <para>
/// For Vulkan each value comes from <c>VkPhysicalDeviceLimits</c> (docs/design/graphics.md section 2). Init-only
/// properties rather than positional parameters, so limits can be added without breaking construction;
/// <c>default(GraphicsLimits)</c> has every limit at zero.
/// </para>
/// <para>
/// Equality compares every field; the floating-point fields compare exactly, because the values come from the
/// backend unchanged. Immutable and safe to use from any thread. No member allocates, except <see cref="ToString"/>,
/// which formats with the invariant culture.
/// </para>
/// </remarks>
public readonly record struct GraphicsLimits
{
    /// <summary>The largest width or height of a 2D texture, in texels.</summary>
    public int MaxTextureDimension2D { get; init; }

    /// <summary>The largest number of layers in an array texture.</summary>
    public int MaxTextureArrayLayers { get; init; }

    /// <summary>The largest number of bind groups a pipeline layout can use at once.</summary>
    public int MaxBindGroups { get; init; }

    /// <summary>The largest push constant range, in bytes.</summary>
    public int MaxPushConstantBytes { get; init; }

    /// <summary>The largest number of color targets in one render pass.</summary>
    public int MaxColorTargets { get; init; }

    /// <summary>The alignment, in bytes, that a uniform buffer binding's offset must be a multiple of.</summary>
    public long MinUniformBufferOffsetAlignment { get; init; }

    /// <summary>The alignment, in bytes, that a storage buffer binding's offset must be a multiple of.</summary>
    public long MinStorageBufferOffsetAlignment { get; init; }

    /// <summary>The largest sampler anisotropy; meaningful only with <see cref="GraphicsFeatures.SamplerAnisotropy"/>.</summary>
    public float MaxSamplerAnisotropy { get; init; }

    /// <summary>
    /// The sample counts color targets support in general. Whether a particular format supports multisampling is
    /// negotiated per format, through its <see cref="FormatSupport.Multisample"/> flag.
    /// </summary>
    public SampleCounts ColorSampleCounts { get; init; }

    /// <summary>
    /// The nanoseconds per timestamp tick; <c>0</c> unless <see cref="GraphicsFeatures.TimestampQueries"/> is enabled.
    /// </summary>
    public double TimestampPeriodNanoseconds { get; init; }

    /// <summary>
    /// Returns every field in the record's usual form, for example
    /// <c>GraphicsLimits { MaxTextureDimension2D = 16384, ..., MaxSamplerAnisotropy = 16, ColorSampleCounts = X1, X2, X4, X8, TimestampPeriodNanoseconds = 1 }</c>.
    /// </summary>
    /// <returns>A diagnostic string, formatted with the invariant culture whatever the current culture is. Allocates.</returns>
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"GraphicsLimits {{ MaxTextureDimension2D = {MaxTextureDimension2D}, MaxTextureArrayLayers = {MaxTextureArrayLayers}, MaxBindGroups = {MaxBindGroups}, MaxPushConstantBytes = {MaxPushConstantBytes}, MaxColorTargets = {MaxColorTargets}, MinUniformBufferOffsetAlignment = {MinUniformBufferOffsetAlignment}, MinStorageBufferOffsetAlignment = {MinStorageBufferOffsetAlignment}, MaxSamplerAnisotropy = {MaxSamplerAnisotropy}, ColorSampleCounts = {ColorSampleCounts}, TimestampPeriodNanoseconds = {TimestampPeriodNanoseconds} }}");
}
