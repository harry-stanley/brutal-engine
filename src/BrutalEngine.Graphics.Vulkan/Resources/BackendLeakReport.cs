using System;
using Brutal.VulkanApi.Vma;
using BrutalEngine.Core.Logging;
using BrutalEngine.Graphics.Vulkan.Memory;

namespace BrutalEngine.Graphics.Vulkan.Resources;

/// <summary>
/// The backend's own leak report at device disposal (docs/design/graphics-vulkan.md section 6, "The resource tracker and
/// leak report", and section 5's shutdown order): destroys every object still in the backend's tables, logging each one,
/// and checks that VMA holds no allocation once they are gone.
/// </summary>
/// <remarks>
/// <para>
/// The front end destroys everything it created before it disposes the device (its own leak report, graphics note
/// section 13), so an entry left here is a backend leak: each is logged at <c>Error</c> as
/// <c>Backend leak: texture 'atlas' was still alive at disposal and is destroyed now</c>, with <c>(unnamed)</c> in place
/// of a missing name. Kinds go in section 5's order (textures, then buffers), each kind newest first.
/// </para>
/// <para>
/// Stateless; called on the device's owner thread at disposal, after the device is idle and before the allocator is
/// destroyed. May allocate (log text).
/// </para>
/// </remarks>
internal static class BackendLeakReport
{
    /// <summary>Destroys and logs every texture and buffer still in their tables.</summary>
    /// <param name="textures">The device's textures.</param>
    /// <param name="buffers">The device's buffers.</param>
    /// <param name="log">Where each leak is logged (the device's <c>Graphics.Vulkan</c> category).</param>
    /// <returns>How many objects were destroyed; both tables are empty afterwards.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="textures"/> or <paramref name="buffers"/> is <see langword="null"/>.</exception>
    public static int DestroyAll(TextureFactory textures, BufferFactory buffers, LogCategory log)
    {
        ArgumentNullException.ThrowIfNull(textures);
        ArgumentNullException.ThrowIfNull(buffers);
        int destroyed = 0;
        while (TryGetNewest(textures.Table, out ulong id, out TextureFactory.Entry texture))
        {
            log.Error($"Backend leak: texture {Quoted(texture.Description.DebugName)} was still alive at disposal and is destroyed now");
            _ = textures.Destroy(id);
            destroyed++;
        }

        while (TryGetNewest(buffers.Table, out ulong id, out BufferFactory.Entry buffer))
        {
            log.Error($"Backend leak: buffer {Quoted(buffer.Name)} was still alive at disposal and is destroyed now");
            _ = buffers.Destroy(id);
            destroyed++;
        }

        return destroyed;
    }

    /// <summary>Counts the allocations VMA still holds (<c>vmaCalculateStatistics</c>, <c>Brutal.Vulkan.Vma/Generated/VmaAllocatorEx.gen.cs:88</c>).</summary>
    /// <param name="vma">The device's allocator.</param>
    /// <param name="log">Where a non-zero count is logged, at <c>Error</c>, as <c>Backend bug: VMA still holds allocations that no backend table tracks (count 1)</c>.</param>
    /// <returns>
    /// The allocation count over every memory type. After <see cref="DestroyAll"/> anything but 0 is a backend bug: an
    /// allocation that no table tracks. The backend's tests read this count.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="vma"/> is the null handle.</exception>
    public static uint VmaAllocationCount(VmaHandle vma, LogCategory log)
    {
        if (vma.IsNull)
        {
            throw new ArgumentException("The allocator is the null handle.", nameof(vma));
        }

        vma.CalculateStatistics(out VmaTotalStatistics statistics);
        uint count = statistics.Total.Statistics.AllocationCount;
        if (count != 0)
        {
            log.Error($"Backend bug: VMA still holds allocations that no backend table tracks (count {count})");
        }

        return count;
    }

    private static bool TryGetNewest<TEntry>(ObjectTable<TEntry> table, out ulong id, out TEntry entry)
    {
        foreach ((ulong Id, TEntry Entry) newest in table)
        {
            (id, entry) = newest;
            return true;
        }

        (id, entry) = (0, default!);
        return false;
    }

    private static string Quoted(string? name) => string.IsNullOrEmpty(name) ? "(unnamed)" : "'" + name + "'";
}
