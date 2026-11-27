using Brutal.VulkanApi;

namespace BrutalEngine.Graphics.Vulkan.Devices;

/// <summary>
/// Maps an adapter's Vulkan properties to the render interface's capabilities (docs/design/graphics-vulkan.md section 3,
/// "Capabilities"), and reads what a device reports from its adapter once, before the device is created.
/// </summary>
/// <remarks>
/// <see cref="ToLimits"/> takes each <see cref="GraphicsLimits"/> property from the <c>VkPhysicalDeviceLimits</c> field
/// the graphics note lists (docs/design/graphics.md section 2). Stateless and thread-safe. Every member but
/// <see cref="Read"/> allocates nothing; <see cref="Read"/> allocates the capabilities and the format array.
/// </remarks>
internal static class CapabilityMapping
{
    // Every count SampleCounts has: 1 to 16, whose values are Vulkan's bits.
    private const VkSampleCountFlags Mappable = VkSampleCountFlags._1Bit | VkSampleCountFlags._2Bit | VkSampleCountFlags._4Bit
        | VkSampleCountFlags._8Bit | VkSampleCountFlags._16Bit;

    /// <summary>Maps the limits.</summary>
    /// <param name="limits">The adapter's limits.</param>
    /// <param name="timestamps">Whether <see cref="GraphicsFeatures.TimestampQueries"/> is enabled.</param>
    /// <returns>
    /// The limits: <c>maxImageDimension2D</c>, <c>maxImageArrayLayers</c>, <c>maxBoundDescriptorSets</c>,
    /// <c>maxPushConstantsSize</c>, <c>maxColorAttachments</c>, the two offset alignments, <c>maxSamplerAnisotropy</c>,
    /// <c>framebufferColorSampleCounts</c> (<see cref="ToSampleCounts"/>), and <c>timestampPeriod</c> only with
    /// <paramref name="timestamps"/>, else 0.
    /// </returns>
    public static GraphicsLimits ToLimits(in VkPhysicalDeviceLimits limits, bool timestamps) => new()
    {
        MaxTextureDimension2D = limits.MaxImageDimension2D,
        MaxTextureArrayLayers = limits.MaxImageArrayLayers,
        MaxBindGroups = limits.MaxBoundDescriptorSets,
        MaxPushConstantBytes = limits.MaxPushConstantsSize,
        MaxColorTargets = limits.MaxColorAttachments,
        MinUniformBufferOffsetAlignment = (long)limits.MinUniformBufferOffsetAlignment.Value,
        MinStorageBufferOffsetAlignment = (long)limits.MinStorageBufferOffsetAlignment.Value,
        MaxSamplerAnisotropy = limits.MaxSamplerAnisotropy,
        ColorSampleCounts = ToSampleCounts(limits.FramebufferColorSampleCounts),
        TimestampPeriodNanoseconds = timestamps ? limits.TimestampPeriod : 0,
    };

    /// <summary>Maps the negotiated features.</summary>
    /// <param name="features">The negotiated features: the required set plus the optional ones the adapter has.</param>
    /// <returns>The enabled features.</returns>
    public static GraphicsFeatures ToFeatures(in FeatureChain.NegotiatedFeatures features) => features.Enabled;

    /// <summary>Maps Vulkan sample counts; the 32 and 64 counts, which <see cref="SampleCounts"/> doesn't have, are dropped.</summary>
    /// <param name="counts">The Vulkan counts.</param>
    /// <returns>The counts from 1 to 16 that <paramref name="counts"/> has.</returns>
    public static SampleCounts ToSampleCounts(VkSampleCountFlags counts) => (SampleCounts)(int)(counts & Mappable);

    /// <summary>Reads what a device on the adapter reports: its capabilities, format support and portability restrictions.</summary>
    /// <typeparam name="TSource">The adapter's queries; the real ones are a struct, so nothing is boxed.</typeparam>
    /// <param name="source">The adapter's queries.</param>
    /// <param name="features">The features the device enables.</param>
    /// <param name="portabilitySubset">Whether the device enables <c>VK_KHR_portability_subset</c>; only then is it read.</param>
    /// <returns>The report. Every query is a void command, so reading can't fail.</returns>
    public static unsafe Report Read<TSource>(TSource source, in FeatureChain.NegotiatedFeatures features, bool portabilitySubset)
        where TSource : ISource
    {
        // Sound: every structure is a stack local of this method, so the chains' pointers stay valid during the queries,
        // which fill them in place and keep no pointer. The device is Vulkan 1.3 (the adapter rules), so the 1.2
        // properties are core, and the portability structures are chained only with their extension.
        VkPhysicalDevicePortabilitySubsetPropertiesKHR portabilityProperties = new();
        VkPhysicalDeviceVulkan12Properties vulkan12 = new() { Next = portabilitySubset ? &portabilityProperties : null };
        VkPhysicalDeviceProperties2 properties = new() { Next = &vulkan12 };
        source.GetProperties2(&properties);
        PortabilityRestrictions portability = PortabilityRestrictions.None;
        if (portabilitySubset)
        {
            VkPhysicalDevicePortabilitySubsetFeaturesKHR portabilityFeatures = new();
            VkPhysicalDeviceFeatures2 chain = new() { Next = &portabilityFeatures };
            source.GetFeatures2(&chain);
            portability = PortabilityRestrictions.Read(portabilityFeatures, portabilityProperties);
        }

        ref VkPhysicalDeviceLimits limits = ref properties.Properties.Limits;
        GraphicsCapabilities capabilities = new()
        {
            Features = ToFeatures(features),
            Limits = ToLimits(limits, features.Has(GraphicsFeatures.TimestampQueries)),
        };
        return new Report(capabilities, FormatTable.Build(source, limits, vulkan12.FramebufferIntegerColorSampleCounts), portability);
    }

    /// <summary>The adapter queries <see cref="Read"/> makes, so tests can script an adapter.</summary>
    internal unsafe interface ISource : FormatTable.ISource
    {
        /// <summary><c>vkGetPhysicalDeviceProperties2</c>.</summary>
        /// <param name="properties">The structure to fill, with its chain.</param>
        public void GetProperties2(VkPhysicalDeviceProperties2* properties);

        /// <summary><c>vkGetPhysicalDeviceFeatures2</c>.</summary>
        /// <param name="features">The structure to fill, with its chain.</param>
        public void GetFeatures2(VkPhysicalDeviceFeatures2* features);
    }

    /// <summary>What a device reports, read once from its adapter.</summary>
    /// <param name="Capabilities">The enabled features and the limits.</param>
    /// <param name="Formats">The support of each <see cref="TextureFormat"/>, indexed by its value (<see cref="FormatTable.Build"/>).</param>
    /// <param name="Portability">The portability restrictions, or <see cref="PortabilityRestrictions.None"/> without the extension.</param>
    /// <remarks>Immutable, provided nobody changes the array; safe to read from any thread.</remarks>
    internal readonly record struct Report(GraphicsCapabilities Capabilities, FormatSupport[] Formats, PortabilityRestrictions Portability);
}
