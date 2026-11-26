using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Numerics;

namespace BrutalEngine.Graphics.Validation;

/// <summary>
/// The front end's pure checks of resource descriptions: every rule that docs/design/graphics.md section 5 makes an
/// exception, so the device only calls these before handing a description to the backend.
/// </summary>
/// <remarks>
/// <para>
/// A structurally invalid description throws <see cref="ArgumentException"/>, or
/// <see cref="ArgumentOutOfRangeException"/> for a value out of its range (including a value outside its enum's
/// members). A limit exceeded throws <see cref="ArgumentOutOfRangeException"/>; a feature not enabled throws
/// <see cref="InvalidOperationException"/>. Failures that depend on the hardware (format support, memory) are the
/// device's <c>Result</c>s, not checked here. Every exception's <see cref="ArgumentException.ParamName"/> is
/// <c>description</c>, the name of both this class's and the device methods' parameter, and its message names the field and the debug name.
/// </para>
/// <para>
/// The limits and features are inputs, so each check depends only on its arguments. Stateless and safe to call from
/// any thread. A valid description allocates nothing; only the failure paths build messages.
/// </para>
/// </remarks>
internal static class ResourceValidator
{
    private const BufferUsage AllBufferUsages = BufferUsage.Vertex | BufferUsage.Index | BufferUsage.Uniform
        | BufferUsage.Storage | BufferUsage.Indirect | BufferUsage.CopySource | BufferUsage.CopyDestination;

    private const TextureUsage AllTextureUsages = TextureUsage.Sampled | TextureUsage.Storage | TextureUsage.ColorTarget
        | TextureUsage.DepthStencilTarget | TextureUsage.CopySource | TextureUsage.CopyDestination;

    private const int MaxSampleCount = 16;

    /// <summary>Checks a buffer description's structure.</summary>
    /// <param name="description">The description to check.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="BufferDescription.Size"/> isn't positive, or <see cref="BufferDescription.Usage"/> or
    /// <see cref="BufferDescription.Memory"/> holds a value outside its enum.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The usage is <see cref="BufferUsage.None"/>, or a <see cref="MemoryIntent.Readback"/> buffer has a usage other
    /// than exactly <see cref="BufferUsage.CopyDestination"/>.
    /// </exception>
    public static void Validate(in BufferDescription description)
    {
        const string Type = nameof(BufferDescription);
        if (description.Size <= 0)
        {
            ThrowOutOfRange(nameof(description), Type, nameof(BufferDescription.Size), "must be greater than 0", description.Size, description.DebugName);
        }

        if ((description.Usage & ~AllBufferUsages) != 0)
        {
            ThrowOutOfRange(nameof(description), Type, nameof(BufferDescription.Usage), "has flags that aren't BufferUsage members", (long)description.Usage, description.DebugName);
        }

        if (description.Usage == BufferUsage.None)
        {
            ThrowInvalid(nameof(description), Type, nameof(BufferDescription.Usage), "must have at least one flag", description.DebugName);
        }

        if ((uint)description.Memory > (uint)MemoryIntent.Readback)
        {
            ThrowOutOfRange(nameof(description), Type, nameof(BufferDescription.Memory), "isn't a MemoryIntent member", (long)description.Memory, description.DebugName);
        }

        if (description.Memory == MemoryIntent.Readback && description.Usage != BufferUsage.CopyDestination)
        {
            ThrowInvalid(nameof(description), Type, nameof(BufferDescription.Usage), "must be exactly CopyDestination for a Readback buffer", description.DebugName);
        }
    }

    /// <summary>Checks a texture description's structure, and its size and layers against the adapter's limits.</summary>
    /// <param name="description">The description to check.</param>
    /// <param name="limits">The device's limits.</param>
    /// <param name="features">
    /// The device's enabled features. No texture rule needs one yet; the parameter is part of the design's signature
    /// so feature-gated formats (BC, ASTC) can be checked here without changing callers.
    /// </param>
    /// <exception cref="ArgumentException">
    /// The format is <see cref="TextureFormat.Undefined"/>, the usage is <see cref="TextureUsage.None"/>, or a sample
    /// count above 1 comes with more than one mip level or layer.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A value is outside its enum; a side, the layers or the mip levels aren't positive; the mip levels exceed
    /// <c>floor(log2(max(Width, Height))) + 1</c>; the sample count isn't 1, 2, 4, 8 or 16; or a side or the layers
    /// exceed <see cref="GraphicsLimits.MaxTextureDimension2D"/> or <see cref="GraphicsLimits.MaxTextureArrayLayers"/>.
    /// </exception>
    [SuppressMessage("Style", "IDE0060:Remove unused parameter", Justification = "The design's signature; feature-gated texture formats will read it.")]
    public static void Validate(in TextureDescription description, in GraphicsLimits limits, GraphicsFeatures features)
    {
        const string Type = nameof(TextureDescription);
        string? name = description.DebugName;
        if ((uint)description.Format > (uint)TextureFormat.Depth32Float)
        {
            ThrowOutOfRange(nameof(description), Type, nameof(TextureDescription.Format), "isn't a TextureFormat member", (long)description.Format, name);
        }

        if (description.Format == TextureFormat.Undefined)
        {
            ThrowInvalid(nameof(description), Type, nameof(TextureDescription.Format), "must not be Undefined", name);
        }

        CheckAtLeastOne(nameof(description), Type, nameof(TextureDescription.Width), description.Width, name);
        CheckAtLeastOne(nameof(description), Type, nameof(TextureDescription.Height), description.Height, name);
        CheckAtLeastOne(nameof(description), Type, nameof(TextureDescription.ArrayLayers), description.ArrayLayers, name);
        CheckAtLeastOne(nameof(description), Type, nameof(TextureDescription.MipLevels), description.MipLevels, name);
        int maxMipLevels = MaxMipLevels(description.Width, description.Height);
        if (description.MipLevels > maxMipLevels)
        {
            ThrowOutOfRange(
                nameof(description),
                Type,
                nameof(TextureDescription.MipLevels),
                string.Create(CultureInfo.InvariantCulture, $"must be at most floor(log2(max(Width, Height))) + 1 ({maxMipLevels})"),
                description.MipLevels,
                name);
        }

        int samples = description.SampleCount;
        if (samples < 1 || samples > MaxSampleCount || !BitOperations.IsPow2(samples))
        {
            ThrowOutOfRange(nameof(description), Type, nameof(TextureDescription.SampleCount), "must be 1, 2, 4, 8 or 16", samples, name);
        }

        if (samples > 1 && description.MipLevels > 1)
        {
            ThrowInvalid(nameof(description), Type, nameof(TextureDescription.SampleCount), "must be 1 when MipLevels is above 1", name);
        }

        if (samples > 1 && description.ArrayLayers > 1)
        {
            ThrowInvalid(nameof(description), Type, nameof(TextureDescription.SampleCount), "must be 1 when ArrayLayers is above 1", name);
        }

        if ((description.Usage & ~AllTextureUsages) != 0)
        {
            ThrowOutOfRange(nameof(description), Type, nameof(TextureDescription.Usage), "has flags that aren't TextureUsage members", (long)description.Usage, name);
        }

        if (description.Usage == TextureUsage.None)
        {
            ThrowInvalid(nameof(description), Type, nameof(TextureDescription.Usage), "must have at least one flag", name);
        }

        CheckAtMost(nameof(description), Type, nameof(TextureDescription.Width), description.Width, limits.MaxTextureDimension2D, nameof(GraphicsLimits.MaxTextureDimension2D), name);
        CheckAtMost(nameof(description), Type, nameof(TextureDescription.Height), description.Height, limits.MaxTextureDimension2D, nameof(GraphicsLimits.MaxTextureDimension2D), name);
        CheckAtMost(nameof(description), Type, nameof(TextureDescription.ArrayLayers), description.ArrayLayers, limits.MaxTextureArrayLayers, nameof(GraphicsLimits.MaxTextureArrayLayers), name);
    }

    /// <summary>Checks that a texture-view description lies inside the texture it views.</summary>
    /// <param name="description">The description to check.</param>
    /// <param name="target">The description of the texture the view's handle refers to; already valid.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The dimension is outside its enum; a base is negative or not below the texture's mip levels or layers; or a count
    /// is below 1 or reaches past the texture's last mip level or layer.
    /// </exception>
    /// <exception cref="ArgumentException">A <see cref="TextureViewDimension.Texture2D"/> view has more than one layer.</exception>
    public static void Validate(in TextureViewDescription description, in TextureDescription target)
    {
        const string Type = nameof(TextureViewDescription);
        string? name = description.DebugName;
        if ((uint)description.Dimension > (uint)TextureViewDimension.Texture2DArray)
        {
            ThrowOutOfRange(nameof(description), Type, nameof(TextureViewDescription.Dimension), "isn't a TextureViewDimension member", (long)description.Dimension, name);
        }

        CheckRange(nameof(description), Type, nameof(TextureViewDescription.BaseMipLevel), nameof(TextureViewDescription.MipLevelCount), description.BaseMipLevel, description.MipLevelCount, target.MipLevels, "mip levels", name);
        CheckRange(nameof(description), Type, nameof(TextureViewDescription.BaseArrayLayer), nameof(TextureViewDescription.ArrayLayerCount), description.BaseArrayLayer, description.ArrayLayerCount, target.ArrayLayers, "layers", name);
        if (description.Dimension == TextureViewDimension.Texture2D && description.ArrayLayerCount > 1)
        {
            ThrowInvalid(nameof(description), Type, nameof(TextureViewDescription.ArrayLayerCount), "must be 1 for a Texture2D view", name);
        }
    }

    /// <summary>Checks a sampler description's structure, and its anisotropy against the device's features and limits.</summary>
    /// <param name="description">The description to check.</param>
    /// <param name="limits">The device's limits.</param>
    /// <param name="features">The device's enabled features.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A filter, address mode, border color or comparison is outside its enum; <see cref="SamplerDescription.MaxAnisotropy"/>
    /// is zero, negative or NaN; or, with the feature enabled, it exceeds <see cref="GraphicsLimits.MaxSamplerAnisotropy"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// <see cref="SamplerDescription.MaxAnisotropy"/> is above 1 without <see cref="GraphicsFeatures.SamplerAnisotropy"/>.
    /// Checked before the limit, which is meaningful only with the feature.
    /// </exception>
    public static void Validate(in SamplerDescription description, in GraphicsLimits limits, GraphicsFeatures features)
    {
        const string Type = nameof(SamplerDescription);
        string? name = description.DebugName;
        CheckFilter(nameof(description), nameof(SamplerDescription.MinFilter), description.MinFilter, name);
        CheckFilter(nameof(description), nameof(SamplerDescription.MagFilter), description.MagFilter, name);
        CheckFilter(nameof(description), nameof(SamplerDescription.MipmapFilter), description.MipmapFilter, name);
        CheckAddressMode(nameof(description), nameof(SamplerDescription.AddressU), description.AddressU, name);
        CheckAddressMode(nameof(description), nameof(SamplerDescription.AddressV), description.AddressV, name);
        if ((uint)description.BorderColor > (uint)BorderColor.OpaqueWhite)
        {
            ThrowOutOfRange(nameof(description), Type, nameof(SamplerDescription.BorderColor), "isn't a BorderColor member", (long)description.BorderColor, name);
        }

        if (description.Compare is { } compare && (uint)compare > (uint)CompareFunction.Always)
        {
            ThrowOutOfRange(nameof(description), Type, nameof(SamplerDescription.Compare), "isn't a CompareFunction member", (long)compare, name);
        }

        float anisotropy = description.MaxAnisotropy;
        // Written as a negation so NaN, which compares false with everything, is rejected too.
        if (!(anisotropy > 0))
        {
            ThrowOutOfRange(nameof(description), Type, nameof(SamplerDescription.MaxAnisotropy), "must be greater than 0", anisotropy, name);
        }

        if (anisotropy > 1)
        {
            if ((features & GraphicsFeatures.SamplerAnisotropy) == 0)
            {
                throw new InvalidOperationException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{Type}.{nameof(SamplerDescription.MaxAnisotropy)} is {anisotropy}, above 1, but the device doesn't have the SamplerAnisotropy feature enabled ({Describe(name)})."));
            }

            if (anisotropy > limits.MaxSamplerAnisotropy)
            {
                ThrowOutOfRange(
                    nameof(description),
                    Type,
                    nameof(SamplerDescription.MaxAnisotropy),
                    string.Create(CultureInfo.InvariantCulture, $"must be at most the device's {nameof(GraphicsLimits.MaxSamplerAnisotropy)} ({limits.MaxSamplerAnisotropy})"),
                    anisotropy,
                    name);
            }
        }
    }

    /// <summary>The longest mip chain for a texture of the given sides: <c>floor(log2(max(width, height))) + 1</c>.</summary>
    /// <param name="width">The width; at least 1.</param>
    /// <param name="height">The height; at least 1.</param>
    /// <returns>The mip level count down to a 1x1 level, from 1 to 31.</returns>
    private static int MaxMipLevels(int width, int height) => BitOperations.Log2((uint)Math.Max(width, height)) + 1;

    private static void CheckAtLeastOne(string paramName, string type, string field, int value, string? debugName)
    {
        if (value < 1)
        {
            ThrowOutOfRange(paramName, type, field, "must be at least 1", value, debugName);
        }
    }

    private static void CheckAtMost(string paramName, string type, string field, int value, int limit, string limitName, string? debugName)
    {
        if (value > limit)
        {
            ThrowOutOfRange(
                paramName,
                type,
                field,
                string.Create(CultureInfo.InvariantCulture, $"must be at most the device's {limitName} ({limit})"),
                value,
                debugName);
        }
    }

    // A subrange [base, base + count) of 0..total: the base inside, the count at least 1 and not past the end.
    private static void CheckRange(string paramName, string type, string baseField, string countField, int first, int count, int total, string what, string? debugName)
    {
        if (first < 0 || first >= total)
        {
            ThrowOutOfRange(
                paramName,
                type,
                baseField,
                string.Create(CultureInfo.InvariantCulture, $"must be from 0 to the texture's {what} minus 1 ({total - 1})"),
                first,
                debugName);
        }

        // total - first can't overflow: total is positive and first is in [0, total).
        if (count < 1 || count > total - first)
        {
            ThrowOutOfRange(
                paramName,
                type,
                countField,
                string.Create(CultureInfo.InvariantCulture, $"must be from 1 to the texture's {what} after {baseField} ({total - first})"),
                count,
                debugName);
        }
    }

    private static void CheckFilter(string paramName, string field, Filter value, string? debugName)
    {
        if ((uint)value > (uint)Filter.Linear)
        {
            ThrowOutOfRange(paramName, nameof(SamplerDescription), field, "isn't a Filter member", (long)value, debugName);
        }
    }

    private static void CheckAddressMode(string paramName, string field, AddressMode value, string? debugName)
    {
        if ((uint)value > (uint)AddressMode.ClampToBorder)
        {
            ThrowOutOfRange(paramName, nameof(SamplerDescription), field, "isn't an AddressMode member", (long)value, debugName);
        }
    }

    [DoesNotReturn]
    private static void ThrowOutOfRange<T>(string paramName, string type, string field, string requirement, T actual, string? debugName) =>
        throw new ArgumentOutOfRangeException(paramName, Message(type, field, requirement, actual, debugName));

    [DoesNotReturn]
    private static void ThrowInvalid(string paramName, string type, string field, string requirement, string? debugName) =>
        throw new ArgumentException(
            string.Create(CultureInfo.InvariantCulture, $"{type}.{field} {requirement} ({Describe(debugName)})."),
            paramName);

    private static string Message<T>(string type, string field, string requirement, T actual, string? debugName) =>
        string.Create(CultureInfo.InvariantCulture, $"{type}.{field} {requirement}, but is {actual} ({Describe(debugName)}).");

    private static string Describe(string? debugName) => debugName is null ? "no debug name" : $"debug name '{debugName}'";
}
