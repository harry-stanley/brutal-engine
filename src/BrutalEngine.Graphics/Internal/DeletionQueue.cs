using System;
using BrutalEngine.Core.Memory;
using BrutalEngine.Graphics.Backend;

namespace BrutalEngine.Graphics.Internal;

/// <summary>
/// One frame slot's deletion queue: the backend objects destroyed while that slot's frame could still use them, destroyed
/// in queue order once the frame completes (docs/design/graphics.md section 1).
/// </summary>
/// <remarks>
/// The entries live in one block from the device's allocator, grown by doubling and kept across frames, so a queue that
/// has grown to the largest frame allocates nothing more. Not thread-safe: the device's owner thread only. No member
/// allocates managed memory, except to throw.
/// </remarks>
internal sealed unsafe class DeletionQueue : IDisposable
{
    private const int InitialCapacity = 64;

    private readonly INativeAllocator _allocator;
    private Entry* _entries;
    private int _capacity;
    private int _count;

    /// <summary>Creates an empty queue with room for 64 objects.</summary>
    /// <param name="allocator">Source of the queue's block. Not owned; must outlive the queue.</param>
    /// <exception cref="OutOfMemoryException">The allocator can't provide the block.</exception>
    internal DeletionQueue(INativeAllocator allocator)
    {
        _allocator = allocator;
        _entries = (Entry*)allocator.Allocate((nuint)(InitialCapacity * sizeof(Entry)), (nuint)sizeof(ulong));
        _capacity = InitialCapacity;
    }

    /// <summary>Makes room for one more object, so the next <see cref="Add"/> can't fail.</summary>
    /// <exception cref="OutOfMemoryException">Growing the block failed; the queue is unchanged.</exception>
    internal void Reserve()
    {
        if (_count < _capacity)
        {
            return;
        }

        int capacity = checked(_capacity * 2);
        var entries = (Entry*)_allocator.Allocate((nuint)capacity * (nuint)sizeof(Entry), (nuint)sizeof(ulong));

        // Sound: both blocks hold at least _count entries and are separate allocations; the old one is freed after the copy.
        new ReadOnlySpan<Entry>(_entries, _count).CopyTo(new Span<Entry>(entries, _count));
        _allocator.Free(_entries);
        _entries = entries;
        _capacity = capacity;
    }

    /// <summary>Queues an object; <see cref="Reserve"/> must have made room first.</summary>
    /// <param name="kind">The object's kind.</param>
    /// <param name="id">The object's backend id.</param>
    internal void Add(BackendObjectKind kind, ulong id)
    {
        // Sound: Reserve left _count < _capacity, so the entry lies inside the block.
        _entries[_count++] = new Entry(kind, id);
    }

    /// <summary>Destroys every queued object on the backend, oldest first, and empties the queue.</summary>
    /// <param name="backend">The device's backend device.</param>
    internal void Retire(IBackendDevice backend)
    {
        for (int i = 0; i < _count; i++)
        {
            // Sound: i < _count <= _capacity.
            backend.Destroy(_entries[i].Kind, _entries[i].Id);
        }

        _count = 0;
    }

    /// <summary>Frees the block without destroying anything. Later calls do nothing.</summary>
    public void Dispose()
    {
        if (_entries != null)
        {
            _allocator.Free(_entries);
            _entries = null;
            _capacity = 0;
            _count = 0;
        }
    }

    private readonly record struct Entry(BackendObjectKind Kind, ulong Id);
}
