using System;
using System.Collections.Generic;
using System.Globalization;
using Brutal.VulkanApi;

namespace BrutalEngine.Graphics.Vulkan.Adapters;

/// <summary>
/// Judges one adapter candidate against the render interface's required set (docs/design/graphics.md section 2;
/// docs/design/graphics-vulkan.md section 3, "Selection"), giving the reasons <c>EnumerateAdapters</c> reports.
/// </summary>
/// <remarks>
/// <para>
/// The rules, in the order their reasons are appended, one reason per failed rule:
/// </para>
/// <list type="number">
/// <item><description>Vulkan 1.3 or later: <c>Vulkan 1.2.0 is below the required 1.3</c>, with the candidate's version.</description></item>
/// <item><description><c>dynamicRendering</c>: <c>dynamicRendering is missing</c>.</description></item>
/// <item><description><c>synchronization2</c>: <c>synchronization2 is missing</c>.</description></item>
/// <item><description>
/// <c>VK_KHR_swapchain</c>, matched exactly and required with or without a compatible surface:
/// <c>VK_KHR_swapchain is missing</c>.
/// </description></item>
/// <item><description>
/// A queue family with graphics support (<see cref="QueueFamilyChoice"/>): <c>no queue family supports graphics</c>, or
/// with a compatible surface, which the family must also present to,
/// <c>no queue family supports graphics and presents to the window</c>.
/// </description></item>
/// <item><description>
/// Each flag of <see cref="RulesRequest.RequiredFeatures"/>, in flag order:
/// <c>SamplerAnisotropy: samplerAnisotropy is missing</c>;
/// <c>TimestampQueries: the graphics queue has no timestamp bits</c> when the chosen family's
/// <c>timestampValidBits</c> is 0, otherwise <c>TimestampQueries: the timestamp period is not positive</c> when
/// <c>timestampPeriod</c> isn't a positive finite number (with no chosen family, the queue family rule has already
/// rejected the candidate and only the period is checked);
/// <c>TextureCompressionBC: textureCompressionBC is missing</c>;
/// <c>TextureCompressionAstc: textureCompressionASTC_LDR is missing</c>.
/// </description></item>
/// </list>
/// <para>
/// Compute support never rejects a candidate. Ranking candidates and applying the adapter selection are the front
/// end's job, not these rules'. Stateless and thread-safe. Runs at load time: it allocates the version reason's string
/// and grows the caller's list.
/// </para>
/// </remarks>
internal static class AdapterRules
{
    private const string DynamicRenderingMissing = "dynamicRendering is missing";
    private const string Synchronization2Missing = "synchronization2 is missing";
    private const string SwapchainMissing = "VK_KHR_swapchain is missing";
    private const string NoGraphicsFamily = "no queue family supports graphics";
    private const string NoGraphicsPresentFamily = "no queue family supports graphics and presents to the window";
    private const string SamplerAnisotropyMissing = "SamplerAnisotropy: samplerAnisotropy is missing";
    private const string NoTimestampBits = "TimestampQueries: the graphics queue has no timestamp bits";
    private const string NoTimestampPeriod = "TimestampQueries: the timestamp period is not positive";
    private const string BCMissing = "TextureCompressionBC: textureCompressionBC is missing";
    private const string AstcMissing = "TextureCompressionAstc: textureCompressionASTC_LDR is missing";

    /// <summary>Appends a reason for every rule <paramref name="candidate"/> fails, in rule order.</summary>
    /// <param name="candidate">The adapter to judge.</param>
    /// <param name="request">What the device request needs from the adapter.</param>
    /// <param name="reasons">The list to append to; nothing is appended when the candidate passes every rule.</param>
    /// <exception cref="ArgumentNullException"><paramref name="reasons"/> is <see langword="null"/>.</exception>
    public static void AppendRejections(in AdapterCandidate candidate, in RulesRequest request, List<string> reasons)
    {
        ArgumentNullException.ThrowIfNull(reasons);

        if (candidate.ApiVersionMajor < 1 || (candidate.ApiVersionMajor == 1 && candidate.ApiVersionMinor < 3))
        {
            reasons.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"Vulkan {candidate.ApiVersionMajor}.{candidate.ApiVersionMinor}.{candidate.ApiVersionPatch} is below the required 1.3"));
        }

        AddIf(reasons, !candidate.DynamicRendering, DynamicRenderingMissing);
        AddIf(reasons, !candidate.Synchronization2, Synchronization2Missing);
        AddIf(reasons, !candidate.DeviceExtensions.Span.Contains(VK.KHR_SWAPCHAIN_EXTENSION_NAME), SwapchainMissing);

        ReadOnlySpan<AdapterCandidate.QueueFamily> families = candidate.QueueFamilies.Span;
        int chosen = QueueFamilyChoice.ChoosePosition(families, request.HasCompatibleSurface);
        AddIf(reasons, chosen < 0, request.HasCompatibleSurface ? NoGraphicsPresentFamily : NoGraphicsFamily);

        GraphicsFeatures required = request.RequiredFeatures;
        AddIf(reasons, Requires(required, GraphicsFeatures.SamplerAnisotropy) && !candidate.SamplerAnisotropy, SamplerAnisotropyMissing);
        if (Requires(required, GraphicsFeatures.TimestampQueries))
        {
            if (chosen >= 0 && families[chosen].TimestampValidBits <= 0)
            {
                reasons.Add(NoTimestampBits);
            }
            else if (!(candidate.TimestampPeriod > 0f) || !float.IsFinite(candidate.TimestampPeriod))
            {
                reasons.Add(NoTimestampPeriod);
            }
        }

        AddIf(reasons, Requires(required, GraphicsFeatures.TextureCompressionBC) && !candidate.TextureCompressionBC, BCMissing);
        AddIf(reasons, Requires(required, GraphicsFeatures.TextureCompressionAstc) && !candidate.TextureCompressionAstcLdr, AstcMissing);
    }

    private static bool Requires(GraphicsFeatures required, GraphicsFeatures flag) => (required & flag) != 0;

    private static void AddIf(List<string> reasons, bool condition, string reason)
    {
        if (condition)
        {
            reasons.Add(reason);
        }
    }

    /// <summary>What a device request needs from an adapter, as far as the rules are concerned.</summary>
    /// <param name="RequiredFeatures">
    /// The features the device must have. The front end has already rejected flags that aren't
    /// <see cref="GraphicsFeatures"/> members; the rules ignore any such flag.
    /// </param>
    /// <param name="HasCompatibleSurface">
    /// Whether the request has a compatible surface, so the chosen queue family must present to it.
    /// </param>
    /// <remarks>Immutable; safe to read from any thread.</remarks>
    internal readonly record struct RulesRequest(GraphicsFeatures RequiredFeatures, bool HasCompatibleSurface);
}
