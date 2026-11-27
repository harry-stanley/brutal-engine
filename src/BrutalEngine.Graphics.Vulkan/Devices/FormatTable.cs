using System;
using Brutal.VulkanApi;

namespace BrutalEngine.Graphics.Vulkan.Devices;

/// <summary>
/// The <see cref="TextureFormat"/> to <c>VkFormat</c> table and per-format support (docs/design/graphics-vulkan.md
/// section 3, "Capabilities").
/// </summary>
/// <remarks>
/// <para>
/// Formats map by name, never by casting. Support comes from <c>optimalTilingFeatures</c>, one flag per feature bit,
/// plus <see cref="FormatSupport.Multisample"/> from the sample counts the specification guarantees an optimal 2D
/// target of the format (<c>VkImageFormatProperties</c>, "sampleCounts"): <c>framebufferDepthSampleCounts</c> for the
/// depth formats, <c>framebufferIntegerColorSampleCounts</c> for the integer formats and
/// <c>framebufferColorSampleCounts</c> for the others. A colour format multisamples at
/// <see cref="GraphicsLimits.ColorSampleCounts"/> (the front end checks the count against it), so an integer format
/// reports <see cref="FormatSupport.Multisample"/> only when its guaranteed counts include all of those.
/// </para>
/// <para>Stateless and thread-safe. Only <see cref="Build"/> allocates: the one array it returns.</para>
/// </remarks>
internal static class FormatTable
{
    /// <summary>The number of formats: every <see cref="TextureFormat"/> but <see cref="TextureFormat.Undefined"/>, valued 1 to this.</summary>
    public const int Count = (int)TextureFormat.Depth32Float;

    /// <summary>Gets a format's Vulkan format.</summary>
    /// <param name="format">A defined format other than <see cref="TextureFormat.Undefined"/>.</param>
    /// <returns>The Vulkan format of the note's table.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="format"/> isn't one of the 16 formats.</exception>
    public static VkFormat ToVk(TextureFormat format) => format switch
    {
        TextureFormat.R8Unorm => VkFormat.R8UNorm,
        TextureFormat.Rg8Unorm => VkFormat.R8G8UNorm,
        TextureFormat.Rgba8Unorm => VkFormat.R8G8B8A8UNorm,
        TextureFormat.Rgba8UnormSrgb => VkFormat.R8G8B8A8SRGB,
        TextureFormat.Bgra8Unorm => VkFormat.B8G8R8A8UNorm,
        TextureFormat.Bgra8UnormSrgb => VkFormat.B8G8R8A8SRGB,
        TextureFormat.R16Float => VkFormat.R16SFloat,
        TextureFormat.Rg16Float => VkFormat.R16G16SFloat,
        TextureFormat.Rgba16Float => VkFormat.R16G16B16A16SFloat,
        TextureFormat.R32Float => VkFormat.R32SFloat,
        TextureFormat.Rg32Float => VkFormat.R32G32SFloat,
        TextureFormat.Rgba32Float => VkFormat.R32G32B32A32SFloat,
        TextureFormat.R32Uint => VkFormat.R32UInt,
        TextureFormat.Rgba8Uint => VkFormat.R8G8B8A8UInt,
        TextureFormat.Depth16Unorm => VkFormat.D16UNorm,
        TextureFormat.Depth32Float => VkFormat.D32SFloat,
        _ => throw new ArgumentOutOfRangeException(nameof(format), (int)format, "Not a defined TextureFormat other than Undefined."),
    };

    /// <summary>Gets the format of a Vulkan format.</summary>
    /// <param name="format">Any Vulkan format.</param>
    /// <returns>The format <see cref="ToVk"/> maps to it, or <see cref="TextureFormat.Undefined"/> for a format outside the table.</returns>
    public static TextureFormat FromVk(VkFormat format)
    {
        for (int value = 1; value <= Count; value++)
        {
            if (ToVk((TextureFormat)value) == format)
            {
                return (TextureFormat)value;
            }
        }

        return TextureFormat.Undefined;
    }

    /// <summary>Works out every format's support, once per device.</summary>
    /// <typeparam name="TSource">The adapter's format query; the real one is a struct, so nothing is boxed.</typeparam>
    /// <param name="source">The adapter's format query.</param>
    /// <param name="limits">The adapter's limits, for the sample counts.</param>
    /// <param name="integerColorSampleCounts">The adapter's <c>framebufferIntegerColorSampleCounts</c> (Vulkan 1.2 properties).</param>
    /// <returns>A new array of <see cref="Count"/> + 1 entries indexed by format value; entry 0 (<see cref="TextureFormat.Undefined"/>) is <see cref="FormatSupport.None"/>.</returns>
    public static unsafe FormatSupport[] Build<TSource>(TSource source, in VkPhysicalDeviceLimits limits, VkSampleCountFlags integerColorSampleCounts)
        where TSource : ISource
    {
        var support = new FormatSupport[Count + 1];
        for (int value = 1; value <= Count; value++)
        {
            var format = (TextureFormat)value;
            bool depth = format is TextureFormat.Depth16Unorm or TextureFormat.Depth32Float;
            VkSampleCountFlags counts = depth ? limits.FramebufferDepthSampleCounts
                : format is TextureFormat.R32Uint or TextureFormat.Rgba8Uint ? integerColorSampleCounts
                : limits.FramebufferColorSampleCounts;

            // Sound: properties is a stack local the query fills in place.
            VkFormatProperties2 properties = new();
            source.GetFormatProperties2(ToVk(format), &properties);
            support[value] = Support(properties.FormatProperties.OptimalTilingFeatures, counts, depth ? VkSampleCountFlags.None : limits.FramebufferColorSampleCounts);
        }

        return support;
    }

    /// <summary>Maps one format's features and sample counts.</summary>
    /// <param name="features">The format's <c>optimalTilingFeatures</c>.</param>
    /// <param name="sampleCounts">The counts a target of the format supports.</param>
    /// <param name="requiredCounts">The counts it must support to be <see cref="FormatSupport.Multisample"/>; <see cref="VkSampleCountFlags.None"/> for none.</param>
    /// <returns>
    /// <see cref="FormatSupport.Sampled"/>, <see cref="FormatSupport.Filterable"/>, <see cref="FormatSupport.ColorTarget"/>,
    /// <see cref="FormatSupport.Blendable"/>, <see cref="FormatSupport.DepthStencilTarget"/>, <see cref="FormatSupport.Storage"/>,
    /// <see cref="FormatSupport.CopySource"/> and <see cref="FormatSupport.CopyDestination"/> for the sampled-image, linear-filter,
    /// colour-attachment, blend, depth-stencil-attachment, storage-image, transfer-source and transfer-destination bits;
    /// <see cref="FormatSupport.Multisample"/> when the format is a colour or depth target, <paramref name="sampleCounts"/>
    /// has every count in <paramref name="requiredCounts"/>, and <paramref name="requiredCounts"/> (or, when it is
    /// <see cref="VkSampleCountFlags.None"/>, <paramref name="sampleCounts"/>) has a count from 2 to 16.
    /// </returns>
    public static FormatSupport Support(VkFormatFeatureFlags features, VkSampleCountFlags sampleCounts, VkSampleCountFlags requiredCounts)
    {
        FormatSupport support = FormatSupport.None;
        support |= (features & VkFormatFeatureFlags.SampledImageBit) != 0 ? FormatSupport.Sampled : FormatSupport.None;
        support |= (features & VkFormatFeatureFlags.SampledImageFilterLinearBit) != 0 ? FormatSupport.Filterable : FormatSupport.None;
        support |= (features & VkFormatFeatureFlags.ColorAttachmentBit) != 0 ? FormatSupport.ColorTarget : FormatSupport.None;
        support |= (features & VkFormatFeatureFlags.ColorAttachmentBlendBit) != 0 ? FormatSupport.Blendable : FormatSupport.None;
        support |= (features & VkFormatFeatureFlags.DepthStencilAttachmentBit) != 0 ? FormatSupport.DepthStencilTarget : FormatSupport.None;
        support |= (features & VkFormatFeatureFlags.StorageImageBit) != 0 ? FormatSupport.Storage : FormatSupport.None;
        support |= (features & VkFormatFeatureFlags.TransferSrcBit) != 0 ? FormatSupport.CopySource : FormatSupport.None;
        support |= (features & VkFormatFeatureFlags.TransferDstBit) != 0 ? FormatSupport.CopyDestination : FormatSupport.None;
        var counts = CapabilityMapping.ToSampleCounts(sampleCounts);
        var required = CapabilityMapping.ToSampleCounts(requiredCounts);
        bool target = (support & (FormatSupport.ColorTarget | FormatSupport.DepthStencilTarget)) != 0;

        // A count above 1 among those a texture may use: the required ones when there are any.
        SampleCounts usable = required == SampleCounts.None ? counts : required;
        bool multisample = target && (counts & required) == required && (usable & ~SampleCounts.X1) != 0;
        return multisample ? support | FormatSupport.Multisample : support;
    }

    /// <summary>The adapter's format query, so tests can script an adapter.</summary>
    internal unsafe interface ISource
    {
        /// <summary><c>vkGetPhysicalDeviceFormatProperties2</c>.</summary>
        /// <param name="format">The format.</param>
        /// <param name="properties">The structure to fill.</param>
        public void GetFormatProperties2(VkFormat format, VkFormatProperties2* properties);
    }
}
