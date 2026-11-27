using System;
using BrutalEngine.Core.Memory;

namespace BrutalEngine.World.Ticks;

/// <summary>
/// The world's list of tick consumers: one slot per query or system, holding its last-run tick, so the world's clamp
/// reaches every consumer in one pass (docs/design/world.md, section 5, "Wrap-safe comparison").
/// </summary>
/// <remarks>
/// <para>
/// <b>Slots.</b> <see cref="Register"/> hands out a slot index and <see cref="Release"/> gives it back; released slots
/// are reused last in, first out before the list grows, so a consumer created and disposed every frame doesn't grow
/// it. Slot indices are dense from 0 and never move.
/// </para>
/// <para>
/// <b>Memory.</b> The slot array comes from the world's allocator, which the list doesn't own. The constructor
/// allocates nothing native; the first <see cref="Register"/> allocates <see cref="InitialCapacity"/> slots, and the
/// array grows by doubling, all or nothing: a failed allocation leaves the list unchanged. <see cref="Dispose"/> frees
/// it. No member allocates managed memory, except to throw.
/// </para>
/// <para>Not thread-safe: the owning world's owner thread is its only caller.</para>
/// </remarks>
internal sealed unsafe class TickConsumers : IDisposable
{
    /// <summary>The capacity after the first growth, in slots.</summary>
    public const int InitialCapacity = 8;

    // What a live slot's Next holds; a free slot holds the next free slot, or EndOfFreeList.
    private const int Live = -2;
    private const int EndOfFreeList = -1;

    private const nuint SlotAlignment = 8;

    private readonly INativeAllocator _allocator;
    private Slot* _slots;
    private int _capacity;
    private int _used;
    private int _count;
    private int _freeHead = EndOfFreeList;
    private bool _disposed;

    /// <summary>Creates an empty list. Allocates no native memory: the array is allocated by the first <see cref="Register"/>.</summary>
    /// <param name="allocator">Source of the slot array: the world's allocator. Not owned; must outlive the list.</param>
    /// <exception cref="ArgumentNullException"><paramref name="allocator"/> is <see langword="null"/>.</exception>
    public TickConsumers(INativeAllocator allocator)
    {
        ArgumentNullException.ThrowIfNull(allocator);
        _allocator = allocator;
    }

    /// <summary>The number of registered (live) consumers. <c>0</c> once disposed.</summary>
    public int Count => _count;

    /// <summary>The number of slots the array holds before it grows. <c>0</c> before the first registration and once disposed.</summary>
    public int Capacity => _capacity;

    /// <summary>Registers a consumer whose last-run tick starts at <paramref name="tick"/>.</summary>
    /// <param name="tick">The initial last-run tick.</param>
    /// <returns>The consumer's slot: the most recently released one if any, otherwise the next unused index.</returns>
    /// <exception cref="ObjectDisposedException">The list is disposed.</exception>
    /// <exception cref="OutOfMemoryException">The allocator can't provide the grown array. The list is unchanged.</exception>
    public int Register(uint tick)
    {
        ThrowIfDisposed();
        int slot;
        if (_freeHead != EndOfFreeList)
        {
            slot = _freeHead;

            // Sound: free-list entries are slots below _used, inside the array.
            _freeHead = _slots[slot].Next;
        }
        else
        {
            if (_used == _capacity)
            {
                Grow();
            }

            slot = _used++;
        }

        // Sound: slot < _used <= _capacity.
        _slots[slot] = new Slot { Tick = tick, Next = Live };
        _count++;
        return slot;
    }

    /// <summary>Releases a consumer's slot, which a later <see cref="Register"/> reuses. Makes no allocator call.</summary>
    /// <param name="slot">A live slot from <see cref="Register"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="slot"/> isn't a live slot. Nothing changes.</exception>
    /// <exception cref="ObjectDisposedException">The list is disposed.</exception>
    public void Release(int slot)
    {
        Slot* entry = LiveSlot(slot);
        entry->Next = _freeHead;
        _freeHead = slot;
        _count--;
    }

    /// <summary>Returns a consumer's last-run tick.</summary>
    /// <param name="slot">A live slot from <see cref="Register"/>.</param>
    /// <returns>The tick, as last set or clamped.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="slot"/> isn't a live slot.</exception>
    /// <exception cref="ObjectDisposedException">The list is disposed.</exception>
    public uint Get(int slot) => LiveSlot(slot)->Tick;

    /// <summary>Sets a consumer's last-run tick, as a query or system does when it completes a run.</summary>
    /// <param name="slot">A live slot from <see cref="Register"/>.</param>
    /// <param name="tick">The new last-run tick.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="slot"/> isn't a live slot. Nothing changes.</exception>
    /// <exception cref="ObjectDisposedException">The list is disposed.</exception>
    public void Set(int slot, uint tick) => LiveSlot(slot)->Tick = tick;

    /// <summary>
    /// Raises every live consumer's tick that is older than <paramref name="floor"/> (wrap-safe) to it; newer ticks and
    /// the floor itself are unchanged. One pass, no allocation.
    /// </summary>
    /// <param name="floor">The consumer floor, <see cref="TickMath.ConsumerFloor"/> of the world's change tick.</param>
    /// <exception cref="ObjectDisposedException">The list is disposed.</exception>
    public void Clamp(uint floor)
    {
        ThrowIfDisposed();
        for (int i = 0; i < _used; i++)
        {
            // Sound: i < _used <= _capacity.
            Slot* entry = _slots + i;
            if (entry->Next == Live && TickMath.IsOlderThanFloor(entry->Tick, floor))
            {
                entry->Tick = floor;
            }
        }
    }

    /// <summary>Frees the slot array. Idempotent; afterwards other members throw <see cref="ObjectDisposedException"/>.</summary>
    /// <remarks>If the allocator's free throws, the exception propagates and the list is still disposed, holding no array.</remarks>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        // Detach first, so the freed array is never reachable from the list.
        _disposed = true;
        Slot* slots = _slots;
        _slots = null;
        _capacity = 0;
        _used = 0;
        _count = 0;
        _freeHead = EndOfFreeList;
        if (slots != null)
        {
            _allocator.Free(slots);
        }
    }

    private void Grow()
    {
        int capacity = _capacity == 0 ? InitialCapacity : checked(_capacity * 2);
        nuint bytes = checked((nuint)(uint)capacity * (nuint)sizeof(Slot));
        var slots = (Slot*)_allocator.Allocate(bytes, SlotAlignment);
        if (_used > 0)
        {
            // Sound: the old array holds _used slots, the new one capacity > _used, and they are separate allocations.
            new ReadOnlySpan<Slot>(_slots, _used).CopyTo(new Span<Slot>(slots, _used));
        }

        // Swap first: if freeing the old array throws, the list is already consistent on the new one.
        Slot* old = _slots;
        _slots = slots;
        _capacity = capacity;
        if (old != null)
        {
            _allocator.Free(old);
        }
    }

    private Slot* LiveSlot(int slot)
    {
        ThrowIfDisposed();

        // Sound: slot is checked against _used <= _capacity before the array is read.
        if ((uint)slot >= (uint)_used || _slots[slot].Next != Live)
        {
            throw new ArgumentOutOfRangeException(nameof(slot), slot, "The slot isn't a registered tick consumer.");
        }

        return _slots + slot;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    /// <summary>One consumer slot: its last-run tick, and its free-list link or the live marker.</summary>
    private struct Slot
    {
        /// <summary>The consumer's last-run tick.</summary>
        public uint Tick;

        /// <summary><see cref="Live"/> for a live slot; otherwise the next free slot or <see cref="EndOfFreeList"/>.</summary>
        public int Next;
    }
}
