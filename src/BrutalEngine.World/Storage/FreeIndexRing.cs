using System;
using BrutalEngine.Core.Memory;

namespace BrutalEngine.World.Storage;

/// <summary>
/// A first-in, first-out queue of free entity-table indices in one unmanaged block: the order in which freed slots
/// are reused, so a slot's generation advances as slowly as possible (docs/design/handles.md, "Rules").
/// </summary>
/// <remarks>
/// <para>
/// The block comes from the allocator given to the constructor, which the ring doesn't own. The ring frees it in
/// <see cref="Dispose"/>, and <see cref="Grow"/> frees the old block once the indices are copied. The ring never
/// grows by itself: its owner grows it with the table, so an <see cref="Enqueue"/> never allocates.
/// </para>
/// <para>Not thread-safe: the owning world's owner thread is its only caller. No member allocates managed memory.</para>
/// </remarks>
internal sealed unsafe class FreeIndexRing : IDisposable
{
    private readonly INativeAllocator _allocator;
    private int* _items;
    private int _capacity;
    private int _head;
    private int _count;

    /// <summary>Creates an empty ring with room for <paramref name="capacity"/> indices.</summary>
    /// <param name="allocator">Source of the block. Not owned; must outlive the ring.</param>
    /// <param name="capacity">Number of indices the ring holds before it must grow. At least 1.</param>
    /// <exception cref="ArgumentNullException"><paramref name="allocator"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> is less than 1.</exception>
    /// <exception cref="OutOfMemoryException">The allocator can't provide the block.</exception>
    public FreeIndexRing(INativeAllocator allocator, int capacity)
    {
        ArgumentNullException.ThrowIfNull(allocator);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);

        _allocator = allocator;
        _items = AllocateBlock(allocator, capacity);
        _capacity = capacity;
    }

    /// <summary>Number of queued indices.</summary>
    public int Count => _count;

    /// <summary>Number of indices the ring holds before it must grow. <c>0</c> once disposed.</summary>
    public int Capacity => _capacity;

    /// <summary>Queues <paramref name="index"/> behind every index already queued.</summary>
    /// <param name="index">The index to queue.</param>
    /// <exception cref="InvalidOperationException">The ring is full; grow it first.</exception>
    /// <exception cref="ObjectDisposedException">The ring is disposed.</exception>
    public void Enqueue(int index)
    {
        ThrowIfDisposed();
        if (_count == _capacity)
        {
            throw new InvalidOperationException($"The free-index ring is full ({_capacity} indices); grow it before queuing more.");
        }

        int tail = _head + _count;
        if (tail >= _capacity)
        {
            tail -= _capacity;
        }

        // Sound: tail is in [0, _capacity), and _items points to a live block of _capacity ints.
        _items[tail] = index;
        _count++;
    }

    /// <summary>Takes the index that has been queued longest.</summary>
    /// <param name="index">The dequeued index, or <c>0</c> when the ring is empty.</param>
    /// <returns><see langword="false"/> if the ring is empty.</returns>
    /// <exception cref="ObjectDisposedException">The ring is disposed.</exception>
    public bool TryDequeue(out int index)
    {
        ThrowIfDisposed();
        if (_count == 0)
        {
            index = 0;
            return false;
        }

        // Sound: _head is in [0, _capacity), and _items points to a live block of _capacity ints.
        index = _items[_head];
        _head++;
        if (_head == _capacity)
        {
            _head = 0;
        }

        _count--;
        return true;
    }

    /// <summary>
    /// Moves the queued indices, in order, to a new block of <paramref name="capacity"/> indices and frees the old
    /// block. If the allocation fails, the ring is unchanged.
    /// </summary>
    /// <param name="capacity">The new capacity. Must be greater than <see cref="Capacity"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> is not greater than <see cref="Capacity"/>.</exception>
    /// <exception cref="ObjectDisposedException">The ring is disposed.</exception>
    /// <exception cref="OutOfMemoryException">The allocator can't provide the new block.</exception>
    public void Grow(int capacity)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(capacity, _capacity);

        int* items = AllocateBlock(_allocator, capacity);

        // Sound: the queued indices occupy _items[_head .. _head + first) and _items[0 .. _count - first), both inside
        // the old block of _capacity ints; the new block holds capacity > _capacity >= _count ints; the blocks are
        // separate allocations, so they don't overlap.
        int first = Math.Min(_count, _capacity - _head);
        new ReadOnlySpan<int>(_items + _head, first).CopyTo(new Span<int>(items, first));
        new ReadOnlySpan<int>(_items, _count - first).CopyTo(new Span<int>(items + first, _count - first));

        _allocator.Free(_items);
        _items = items;
        _capacity = capacity;
        _head = 0;
    }

    /// <summary>Frees the block. Idempotent; afterwards every other member throws <see cref="ObjectDisposedException"/>.</summary>
    public void Dispose()
    {
        if (_items == null)
        {
            return;
        }

        _allocator.Free(_items);
        _items = null;
        _capacity = 0;
        _head = 0;
        _count = 0;
    }

    private static int* AllocateBlock(INativeAllocator allocator, int capacity) =>
        (int*)allocator.Allocate(checked((nuint)(uint)capacity * sizeof(int)), sizeof(int));

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_items == null, this);
}
