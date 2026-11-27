using System;
using System.Diagnostics.CodeAnalysis;
using BrutalEngine.Core;

namespace BrutalEngine.Graphics.Vulkan.Swapchains;

/// <summary>
/// A device's live swapchains by contract id (docs/design/graphics-vulkan.md section 6, "The resource tracker"): each
/// id is a Core <see cref="Handle"/> raw value from the set's own <see cref="HandleAllocator"/>, so an id is never 0 and
/// a stale id is detected, never mistaken for whatever reuses its slot.
/// </summary>
/// <typeparam name="TSwapchain">
/// The component an id maps to; the device facade will use the swapchain component (<c>VulkanSwapchain</c>), tests any reference type.
/// </typeparam>
/// <remarks>
/// The set owns ids, not components: <see cref="Remove"/> hands the component back for its owner to dispose. Not
/// thread-safe: the device's owner thread calls it. <see cref="TryGet"/> and <see cref="Remove"/> allocate nothing;
/// <see cref="Add"/> allocates only when the set grows.
/// </remarks>
internal sealed class SwapchainSet<TSwapchain>
    where TSwapchain : class
{
    private readonly HandleAllocator _ids = new();
    private TSwapchain?[] _items = [];

    /// <summary>Gets the number of live ids.</summary>
    public int Count => _ids.Count;

    /// <summary>Gives a component a new id.</summary>
    /// <param name="swapchain">The component.</param>
    /// <returns>The id; never 0, and never one the set has issued before for another component.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="swapchain"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The allocator has no slot left.</exception>
    public ulong Add(TSwapchain swapchain)
    {
        ArgumentNullException.ThrowIfNull(swapchain);
        Handle id = _ids.Allocate();
        if (_items.Length < _ids.Capacity)
        {
            Array.Resize(ref _items, _ids.Capacity);
        }

        _items[id.Index] = swapchain;
        return id.RawValue;
    }

    /// <summary>Looks up a component by id.</summary>
    /// <param name="id">Any value.</param>
    /// <param name="swapchain">The component when this returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> if <paramref name="id"/> is live in this set; <see langword="false"/> for 0, a removed id or one it never issued.</returns>
    public bool TryGet(ulong id, [NotNullWhen(true)] out TSwapchain? swapchain)
    {
        uint generation = (uint)(id >> 32);
        if (generation == 0 || !_ids.IsValid(new Handle((uint)id, generation)))
        {
            swapchain = null;
            return false;
        }

        swapchain = _items[(uint)id]!;
        return true;
    }

    /// <summary>Retires an id, making it stale, and hands its component back.</summary>
    /// <param name="id">Any value.</param>
    /// <param name="swapchain">The component when this returns <see langword="true"/>; the caller disposes it.</param>
    /// <returns><see langword="true"/> if <paramref name="id"/> was live; otherwise nothing changes.</returns>
    public bool Remove(ulong id, [NotNullWhen(true)] out TSwapchain? swapchain)
    {
        if (!TryGet(id, out swapchain))
        {
            return false;
        }

        _ids.Free(Handle.FromRawValue(id));
        _items[(uint)id] = null;
        return true;
    }
}
