using System;
using Brutal.VulkanApi;

namespace BrutalEngine.Graphics.Vulkan.Resources;

/// <summary>
/// Maps the contract's <see cref="BufferUsage"/> flags to <see cref="VkBufferUsageFlags"/>, one flag to one bit
/// (docs/design/graphics-vulkan.md section 6; the front end has already validated the description, docs/design/graphics.md
/// section 5).
/// </summary>
/// <remarks>
/// <para>The table:</para>
/// <list type="table">
/// <listheader><term>Usage</term><description>Vulkan bit</description></listheader>
/// <item><term><see cref="BufferUsage.Vertex"/></term><description><c>VERTEX_BUFFER</c></description></item>
/// <item><term><see cref="BufferUsage.Index"/></term><description><c>INDEX_BUFFER</c></description></item>
/// <item><term><see cref="BufferUsage.Uniform"/></term><description><c>UNIFORM_BUFFER</c></description></item>
/// <item><term><see cref="BufferUsage.Storage"/></term><description><c>STORAGE_BUFFER</c></description></item>
/// <item><term><see cref="BufferUsage.Indirect"/></term><description><c>INDIRECT_BUFFER</c></description></item>
/// <item><term><see cref="BufferUsage.CopySource"/></term><description><c>TRANSFER_SRC</c></description></item>
/// <item><term><see cref="BufferUsage.CopyDestination"/></term><description><c>TRANSFER_DST</c></description></item>
/// </list>
/// <para>Pure, stateless and thread-safe. Allocates nothing.</para>
/// </remarks>
internal static class BufferUsageMapping
{
    private const BufferUsage Defined = BufferUsage.Vertex | BufferUsage.Index | BufferUsage.Uniform | BufferUsage.Storage
        | BufferUsage.Indirect | BufferUsage.CopySource | BufferUsage.CopyDestination;

    /// <summary>Maps usage flags.</summary>
    /// <param name="usage">The flags; <see cref="BufferUsage.None"/> maps to no bits.</param>
    /// <returns>The Vulkan usage bits, one per flag.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="usage"/> has a bit no <see cref="BufferUsage"/> member has.</exception>
    public static VkBufferUsageFlags Map(BufferUsage usage)
    {
        if ((usage & ~Defined) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(usage), (int)usage, "Not a combination of BufferUsage flags.");
        }

        VkBufferUsageFlags flags = 0;
        flags |= Bit(usage, BufferUsage.Vertex, VkBufferUsageFlags.VertexBufferBit);
        flags |= Bit(usage, BufferUsage.Index, VkBufferUsageFlags.IndexBufferBit);
        flags |= Bit(usage, BufferUsage.Uniform, VkBufferUsageFlags.UniformBufferBit);
        flags |= Bit(usage, BufferUsage.Storage, VkBufferUsageFlags.StorageBufferBit);
        flags |= Bit(usage, BufferUsage.Indirect, VkBufferUsageFlags.IndirectBufferBit);
        flags |= Bit(usage, BufferUsage.CopySource, VkBufferUsageFlags.TransferSrcBit);
        flags |= Bit(usage, BufferUsage.CopyDestination, VkBufferUsageFlags.TransferDstBit);
        return flags;
    }

    private static VkBufferUsageFlags Bit(BufferUsage usage, BufferUsage flag, VkBufferUsageFlags bit) =>
        (usage & flag) != 0 ? bit : 0;
}
