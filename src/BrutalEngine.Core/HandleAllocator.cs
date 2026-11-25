using System;

namespace BrutalEngine.Core;

/// <summary>
/// Issues and validates generational <see cref="Handle"/> values for a pool of slots.
/// </summary>
/// <remarks>
/// <para>
/// Each slot has a generation. Allocating a slot returns a handle carrying the slot's current
/// generation. Freeing the slot advances its generation, so every handle issued for the previous
/// occupant becomes stale and <see cref="IsValid"/> rejects it from then on.
/// </para>
/// <para>
/// Freed slots are reused in first-in, first-out order, which spreads reuse across the pool.
/// When a slot's generation would overflow, the slot is retired instead: it is never reused,
/// so a stale handle can never become valid again by wrapping around.
/// </para>
/// <para>
/// Memory: the allocator stores 9 bytes per slot (generation, free-queue entry and live flag).
/// It allocates managed memory only when it grows past its current capacity. Call
/// <see cref="EnsureCapacity"/> up front to keep steady-state allocation at zero.
/// </para>
/// <para>Thread safety: instances are not thread-safe. Synchronise externally or confine an instance to one thread.</para>
/// </remarks>
public sealed class HandleAllocator
{
    /// <summary>The largest number of slots an allocator can have.</summary>
    public const int MaxSlotLimit = 0x7FFF_FFC7; // Array.MaxLength, the largest array the runtime allows.

    private uint[] _generations;
    private bool[] _live;
    private int[] _freeQueue;
    private int _freeHead;
    private int _freeCount;
    private int _slotCount;
    private int _liveCount;
    private int _retiredCount;

    /// <summary>Creates an allocator.</summary>
    /// <param name="initialCapacity">Number of slots to reserve storage for up front.</param>
    /// <param name="maxSlots">
    /// Upper bound on the number of slots. Allocation fails once all of them are live or retired.
    /// Defaults to <see cref="MaxSlotLimit"/>.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="maxSlots"/> is less than 1 or greater than <see cref="MaxSlotLimit"/>, or
    /// <paramref name="initialCapacity"/> is negative or greater than <paramref name="maxSlots"/>.
    /// </exception>
    public HandleAllocator(int initialCapacity = 0, int maxSlots = MaxSlotLimit)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxSlots, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxSlots, MaxSlotLimit);
        ArgumentOutOfRangeException.ThrowIfNegative(initialCapacity);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(initialCapacity, maxSlots);

        MaxSlots = maxSlots;
        _generations = new uint[initialCapacity];
        _live = new bool[initialCapacity];
        _freeQueue = new int[initialCapacity];
    }

    /// <summary>Upper bound on the number of slots, as passed to the constructor.</summary>
    public int MaxSlots { get; }

    /// <summary>Number of live handles.</summary>
    public int Count => _liveCount;

    /// <summary>Number of slots storage is currently reserved for.</summary>
    public int Capacity => _generations.Length;

    /// <summary>Number of slots permanently retired because their generation was exhausted.</summary>
    public int RetiredCount => _retiredCount;

    /// <summary>Reserves storage for at least <paramref name="capacity"/> slots.</summary>
    /// <param name="capacity">Required capacity.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="capacity"/> is negative or greater than <see cref="MaxSlots"/>.
    /// </exception>
    public void EnsureCapacity(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(capacity, MaxSlots);

        if (capacity > _generations.Length)
        {
            Resize(capacity);
        }
    }

    /// <summary>Allocates a slot and returns its handle.</summary>
    /// <returns>A new, valid handle. Never <see cref="Handle.Null"/>.</returns>
    /// <exception cref="InvalidOperationException">All <see cref="MaxSlots"/> slots are live or retired.</exception>
    public Handle Allocate()
    {
        if (!TryAllocate(out Handle handle))
        {
            throw new InvalidOperationException(
                $"The handle allocator is exhausted: all {MaxSlots} slots are live or retired.");
        }

        return handle;
    }

    /// <summary>Allocates a slot if one is available.</summary>
    /// <param name="handle">The new handle, or <see cref="Handle.Null"/> on failure.</param>
    /// <returns><see langword="false"/> if all <see cref="MaxSlots"/> slots are live or retired.</returns>
    public bool TryAllocate(out Handle handle)
    {
        int index;
        if (_freeCount > 0)
        {
            index = _freeQueue[_freeHead];
            _freeHead = (_freeHead + 1) % _freeQueue.Length;
            _freeCount--;
        }
        else if (_slotCount < MaxSlots)
        {
            if (_slotCount == _generations.Length)
            {
                Grow();
            }

            index = _slotCount++;
            _generations[index] = 1;
        }
        else
        {
            handle = Handle.Null;
            return false;
        }

        _live[index] = true;
        _liveCount++;
        handle = new Handle((uint)index, _generations[index]);
        return true;
    }

    /// <summary>Returns <see langword="true"/> if <paramref name="handle"/> refers to a live slot of this allocator.</summary>
    /// <param name="handle">The handle to check. <see cref="Handle.Null"/> is never valid.</param>
    /// <returns><see langword="true"/> if the handle is live.</returns>
    public bool IsValid(Handle handle)
    {
        uint index = handle.Index;
        return !handle.IsNull
            && index < (uint)_slotCount
            && _live[index]
            && _generations[index] == handle.Generation;
    }

    /// <summary>Frees the slot <paramref name="handle"/> refers to, making every copy of the handle stale.</summary>
    /// <param name="handle">A live handle issued by this allocator.</param>
    /// <exception cref="ArgumentException"><paramref name="handle"/> is <see cref="Handle.Null"/>.</exception>
    /// <exception cref="StaleHandleException"><paramref name="handle"/> is not live (already freed, or never issued by this allocator).</exception>
    public void Free(Handle handle)
    {
        if (handle.IsNull)
        {
            throw new ArgumentException("Cannot free the null handle.", nameof(handle));
        }

        if (!TryFree(handle))
        {
            throw new StaleHandleException(handle);
        }
    }

    /// <summary>Frees the slot <paramref name="handle"/> refers to, if the handle is live.</summary>
    /// <param name="handle">The handle to free.</param>
    /// <returns><see langword="false"/> if the handle is null or not live. Nothing changes in that case.</returns>
    public bool TryFree(Handle handle)
    {
        if (!IsValid(handle))
        {
            return false;
        }

        Release((int)handle.Index);
        return true;
    }

    /// <summary>Frees every live slot. All previously issued handles become stale.</summary>
    /// <remarks>Freed slots are queued for reuse in index order. Storage is kept.</remarks>
    public void Clear()
    {
        for (int index = 0; index < _slotCount; index++)
        {
            if (_live[index])
            {
                Release(index);
            }
        }
    }

    /// <summary>Test seam: overrides a slot's generation so generation exhaustion can be tested.</summary>
    internal void SetGenerationForTesting(int index, uint generation)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _slotCount);
        ArgumentOutOfRangeException.ThrowIfZero(generation);

        _generations[index] = generation;
    }

    private void Release(int index)
    {
        _live[index] = false;
        _liveCount--;

        if (_generations[index] == uint.MaxValue)
        {
            // Advancing would wrap to 0 (reserved) and then revisit old generations. Retire the slot instead.
            _retiredCount++;
            return;
        }

        _generations[index]++;
        int tail = (_freeHead + _freeCount) % _freeQueue.Length;
        _freeQueue[tail] = index;
        _freeCount++;
    }

    private void Grow()
    {
        long doubled = Math.Max(4L, (long)_generations.Length * 2);
        Resize((int)Math.Min(doubled, MaxSlots));
    }

    private void Resize(int capacity)
    {
        Array.Resize(ref _generations, capacity);
        Array.Resize(ref _live, capacity);

        // Linearise the circular free queue into the new array, preserving FIFO order.
        int[] queue = new int[capacity];
        for (int i = 0; i < _freeCount; i++)
        {
            queue[i] = _freeQueue[(_freeHead + i) % _freeQueue.Length];
        }

        _freeQueue = queue;
        _freeHead = 0;
    }
}
