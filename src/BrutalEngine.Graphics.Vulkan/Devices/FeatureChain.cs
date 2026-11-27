using System;
using Brutal.VulkanApi;
using BrutalEngine.Graphics.Vulkan.Adapters;

namespace BrutalEngine.Graphics.Vulkan.Devices;

/// <summary>
/// Negotiates a logical device's optional features and writes its feature chain (docs/design/graphics-vulkan.md section 3,
/// "Device extensions and the feature chain"): <c>VkPhysicalDeviceFeatures2</c>, then <c>VkPhysicalDeviceVulkan13Features</c>,
/// then <c>VkPhysicalDeviceSwapchainMaintenance1FeaturesKHR</c> when swapchain maintenance1 is used.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Negotiate"/> answers from what the candidate already holds (<see cref="CandidateReader"/>), so it queries
/// nothing. A feature counts as present exactly as the adapter rules judge it (<see cref="AdapterRules"/>):
/// <c>TimestampQueries</c> needs the chosen family's <c>timestampValidBits</c> above 0 and a positive, finite
/// <c>timestampPeriod</c>.
/// </para>
/// <para>
/// The Vulkan 1.3 structure is always chained, with <c>dynamicRendering</c> and <c>synchronization2</c>: the rules accept
/// only Vulkan 1.3 adapters, where it is core and both features are required. The swapchain maintenance1 feature is
/// enabled with its extension, which the specification's feature requirements make it supported with (cited, not
/// observed on a driver that lacks it).
/// </para>
/// <para>Stateless and thread-safe. Both members allocate nothing, except <see cref="Negotiate"/>'s exceptions.</para>
/// </remarks>
internal static class FeatureChain
{
    /// <summary>Every <see cref="GraphicsFeatures"/> flag this backend negotiates.</summary>
    internal const GraphicsFeatures Negotiable = GraphicsFeatures.SamplerAnisotropy | GraphicsFeatures.TimestampQueries
        | GraphicsFeatures.TextureCompressionBC | GraphicsFeatures.TextureCompressionAstc;

    /// <summary>Works out the features a device on this adapter enables.</summary>
    /// <param name="candidate">The adapter, as read during enumeration.</param>
    /// <param name="queueFamily">The Vulkan index of the queue family the device uses, one of the candidate's families.</param>
    /// <param name="required">The features the device must have; flags outside <see cref="Negotiable"/> are ignored.</param>
    /// <param name="optional">The features to enable when present; flags outside <see cref="Negotiable"/> are ignored.</param>
    /// <returns>Every required flag, and each optional flag the adapter has; nothing else.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="queueFamily"/> isn't one of the candidate's families, or the adapter lacks a required feature
    /// (the adapter rules reject such an adapter first).
    /// </exception>
    public static NegotiatedFeatures Negotiate(in AdapterCandidate candidate, int queueFamily, GraphicsFeatures required, GraphicsFeatures optional)
    {
        ReadOnlySpan<AdapterCandidate.QueueFamily> families = candidate.QueueFamilies.Span;
        int position = -1;
        for (int i = 0; i < families.Length && position < 0; i++)
        {
            position = families[i].Index == queueFamily ? i : -1;
        }

        if (position < 0)
        {
            throw new ArgumentException("The queue family isn't one of the adapter's.", nameof(queueFamily));
        }

        int timestampBits = families[position].TimestampValidBits;

        GraphicsFeatures present = GraphicsFeatures.None;
        present |= candidate.SamplerAnisotropy ? GraphicsFeatures.SamplerAnisotropy : GraphicsFeatures.None;
        present |= timestampBits > 0 && candidate.TimestampPeriod > 0f && float.IsFinite(candidate.TimestampPeriod) ? GraphicsFeatures.TimestampQueries : GraphicsFeatures.None;
        present |= candidate.TextureCompressionBC ? GraphicsFeatures.TextureCompressionBC : GraphicsFeatures.None;
        present |= candidate.TextureCompressionAstcLdr ? GraphicsFeatures.TextureCompressionAstc : GraphicsFeatures.None;
        required &= Negotiable;
        if ((required & ~present) != 0)
        {
            throw new ArgumentException("The adapter lacks a required feature; the adapter rules reject such an adapter.", nameof(required));
        }

        return new NegotiatedFeatures(required | (optional & present));
    }

    /// <summary>Writes the feature chain into caller memory and returns its head, for <c>VkDeviceCreateInfo.pNext</c>.</summary>
    /// <param name="storage">
    /// Where the structures go: a stack local of the caller that outlives device creation, which keeps no pointer to it.
    /// Every structure in it is overwritten.
    /// </param>
    /// <param name="features">The negotiated features.</param>
    /// <param name="swapchainMaintenance1">Whether a swapchain maintenance1 extension is enabled, so its feature is chained.</param>
    /// <returns>The head of the chain, <c>&amp;storage-&gt;Features2</c>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="storage"/> is null.</exception>
    public static unsafe VkPhysicalDeviceFeatures2* Fill(Storage* storage, in NegotiatedFeatures features, bool swapchainMaintenance1)
    {
        if (storage is null)
        {
            throw new ArgumentNullException(nameof(storage));
        }

        // Sound: storage points at the caller's live local, and every link points into that same value, so the chain is
        // valid for as long as the caller keeps it in place, which outlives the creation it is passed to.
        storage->SwapchainMaintenance1 = new() { SwapchainMaintenance1 = true };
        storage->Vulkan13 = new()
        {
            Next = swapchainMaintenance1 ? &storage->SwapchainMaintenance1 : null,
            DynamicRendering = true,
            Synchronization2 = true,
        };
        storage->Features2 = new() { Next = &storage->Vulkan13 };
        storage->Features2.Features.SamplerAnisotropy = features.Has(GraphicsFeatures.SamplerAnisotropy);
        storage->Features2.Features.TextureCompressionBC = features.Has(GraphicsFeatures.TextureCompressionBC);
        storage->Features2.Features.TextureCompressionASTCLDR = features.Has(GraphicsFeatures.TextureCompressionAstc);
        return &storage->Features2;
    }

    /// <summary>The negotiated features of a device.</summary>
    /// <param name="Enabled">The enabled <see cref="GraphicsFeatures"/>: the required set plus the present optional ones.</param>
    /// <remarks>Immutable; safe to read from any thread.</remarks>
    internal readonly record struct NegotiatedFeatures(GraphicsFeatures Enabled)
    {
        /// <summary>Whether a feature is enabled.</summary>
        /// <param name="feature">One flag.</param>
        /// <returns><see langword="true"/> if every bit of <paramref name="feature"/> is enabled.</returns>
        public bool Has(GraphicsFeatures feature) => (Enabled & feature) == feature;
    }

    /// <summary>The structures of one feature chain, held together so one stack local holds them all.</summary>
    /// <remarks>Written by <see cref="Fill"/>; the links point into the same value, so it must not be copied or moved.</remarks>
    internal struct Storage
    {
        /// <summary>The head: the core features.</summary>
        public VkPhysicalDeviceFeatures2 Features2;

        /// <summary>The Vulkan 1.3 features.</summary>
        public VkPhysicalDeviceVulkan13Features Vulkan13;

        /// <summary>The swapchain maintenance1 feature, linked only when it is used.</summary>
        public VkPhysicalDeviceSwapchainMaintenance1FeaturesKHR SwapchainMaintenance1;
    }
}
