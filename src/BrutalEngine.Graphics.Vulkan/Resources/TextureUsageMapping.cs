using System;
using Brutal.VulkanApi;

namespace BrutalEngine.Graphics.Vulkan.Resources;

/// <summary>
/// Maps the contract's <see cref="TextureUsage"/> flags to <see cref="VkImageUsageFlags"/>, one flag to one bit
/// (docs/design/graphics-vulkan.md section 6; the front end has already validated the description, docs/design/graphics.md
/// section 5).
/// </summary>
/// <remarks>
/// <para>The table:</para>
/// <list type="table">
/// <listheader><term>Usage</term><description>Vulkan bit</description></listheader>
/// <item><term><see cref="TextureUsage.Sampled"/></term><description><c>SAMPLED</c></description></item>
/// <item><term><see cref="TextureUsage.Storage"/></term><description><c>STORAGE</c></description></item>
/// <item><term><see cref="TextureUsage.ColorTarget"/></term><description><c>COLOR_ATTACHMENT</c></description></item>
/// <item><term><see cref="TextureUsage.DepthStencilTarget"/></term><description><c>DEPTH_STENCIL_ATTACHMENT</c></description></item>
/// <item><term><see cref="TextureUsage.CopySource"/></term><description><c>TRANSFER_SRC</c></description></item>
/// <item><term><see cref="TextureUsage.CopyDestination"/></term><description><c>TRANSFER_DST</c></description></item>
/// </list>
/// <para>Pure, stateless and thread-safe. Allocates nothing.</para>
/// </remarks>
internal static class TextureUsageMapping
{
    private const TextureUsage Defined = TextureUsage.Sampled | TextureUsage.Storage | TextureUsage.ColorTarget
        | TextureUsage.DepthStencilTarget | TextureUsage.CopySource | TextureUsage.CopyDestination;

    /// <summary>Maps usage flags.</summary>
    /// <param name="usage">The flags; <see cref="TextureUsage.None"/> maps to no bits.</param>
    /// <returns>The Vulkan usage bits, one per flag.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="usage"/> has a bit no <see cref="TextureUsage"/> member has.</exception>
    public static VkImageUsageFlags Map(TextureUsage usage)
    {
        if ((usage & ~Defined) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(usage), (int)usage, "Not a combination of TextureUsage flags.");
        }

        VkImageUsageFlags flags = 0;
        flags |= Bit(usage, TextureUsage.Sampled, VkImageUsageFlags.SampledBit);
        flags |= Bit(usage, TextureUsage.Storage, VkImageUsageFlags.StorageBit);
        flags |= Bit(usage, TextureUsage.ColorTarget, VkImageUsageFlags.ColorAttachmentBit);
        flags |= Bit(usage, TextureUsage.DepthStencilTarget, VkImageUsageFlags.DepthStencilAttachmentBit);
        flags |= Bit(usage, TextureUsage.CopySource, VkImageUsageFlags.TransferSrcBit);
        flags |= Bit(usage, TextureUsage.CopyDestination, VkImageUsageFlags.TransferDstBit);
        return flags;
    }

    private static VkImageUsageFlags Bit(TextureUsage usage, TextureUsage flag, VkImageUsageFlags bit) =>
        (usage & flag) != 0 ? bit : 0;
}
