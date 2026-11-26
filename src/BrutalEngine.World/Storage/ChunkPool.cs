using System;
using BrutalEngine.Core.Memory;

namespace BrutalEngine.World.Storage;

/// <summary>
/// A world's pool of chunk blocks: whole blocks of one size, 64-byte aligned, rented by archetypes and returned when
/// they empty (docs/design/world.md, section 4, "Chunk pool").
/// </summary>
/// <remarks>
/// <para>
/// <b>Reuse.</b> <see cref="Rent"/> takes the most recently returned block, and asks the allocator only when none is
/// pooled; <see cref="Return"/> keeps the block for reuse. Steady churn therefore makes no allocator calls.
/// <see cref="Trim"/> frees every pooled block (<c>EcsWorld.TrimMemory</c>).
/// </para>
/// <para>
/// <b>Memory.</b> Blocks come from the allocator given to the constructor, which the pool doesn't own. The free list
/// is intrusive: a pooled block's first 8 bytes hold the next pooled block, so pooling needs no other memory and no
/// member allocates managed memory, except to throw. Rented blocks belong to their renter, which returns them before
/// the pool is disposed; <see cref="Dispose"/> frees the pooled blocks. A rented block's contents are uninitialised.
/// </para>
/// <para>Not thread-safe: the owning world's owner thread is its only caller.</para>
/// </remarks>
internal sealed unsafe class ChunkPool : IDisposable
{
    /// <summary>The alignment of every block, in bytes: the largest component alignment.</summary>
    public const int BlockAlignment = ComponentRegistry.MaxComponentAlignment;

    private readonly INativeAllocator _allocator;
    private byte* _head;
    private bool _disposed;

    /// <summary>Creates an empty pool of <paramref name="chunkBytes"/>-byte blocks. Allocates nothing.</summary>
    /// <param name="allocator">Source of the blocks. Not owned; must outlive the pool.</param>
    /// <param name="chunkBytes">The block size in bytes, at least 8 (the free-list link).</param>
    /// <exception cref="ArgumentNullException"><paramref name="allocator"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="chunkBytes"/> is under 8.</exception>
    public ChunkPool(INativeAllocator allocator, int chunkBytes)
    {
        ArgumentNullException.ThrowIfNull(allocator);
        ArgumentOutOfRangeException.ThrowIfLessThan(chunkBytes, sizeof(byte*));
        _allocator = allocator;
        ChunkBytes = chunkBytes;
    }

    /// <summary>The size of every block, in bytes.</summary>
    public int ChunkBytes { get; }

    /// <summary>The number of returned blocks held for reuse.</summary>
    public int PooledCount { get; private set; }

    /// <summary>The number of blocks rented and not yet returned.</summary>
    public int RentedCount { get; private set; }

    /// <summary>Takes a pooled block, or allocates one if none is pooled.</summary>
    /// <returns>A block of <see cref="ChunkBytes"/> bytes, 64-byte aligned, with uninitialised contents.</returns>
    /// <exception cref="ObjectDisposedException">The pool is disposed.</exception>
    /// <exception cref="OutOfMemoryException">The allocator can't provide a block. The pool is unchanged.</exception>
    public byte* Rent()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        byte* block = _head;
        if (block != null)
        {
            // Sound: a pooled block holds at least ChunkBytes >= 8 bytes, and its first 8 hold the next pooled block,
            // written by Return; 64-byte alignment makes the pointer read aligned.
            _head = *(byte**)block;
            PooledCount--;
        }
        else
        {
            block = (byte*)_allocator.Allocate((nuint)ChunkBytes, BlockAlignment);
        }

        RentedCount++;
        return block;
    }

    /// <summary>Keeps a rented block for reuse. Makes no allocator call.</summary>
    /// <param name="block">A block from <see cref="Rent"/> on this pool, not returned since.</param>
    /// <exception cref="ArgumentNullException"><paramref name="block"/> is null.</exception>
    /// <exception cref="InvalidOperationException">No block is rented, so <paramref name="block"/> can't be one.</exception>
    /// <exception cref="ObjectDisposedException">The pool is disposed.</exception>
    /// <remarks>Other foreign or repeated blocks aren't detected; the caller's chunk table only returns its own.</remarks>
    public void Return(byte* block)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (block == null)
        {
            throw new ArgumentNullException(nameof(block));
        }

        if (RentedCount == 0)
        {
            throw new InvalidOperationException("No chunk block is rented from this pool, so none can be returned.");
        }

        // Sound: the block is a rented block of ChunkBytes >= 8 bytes, 64-byte aligned, which the pool now owns.
        *(byte**)block = _head;
        _head = block;
        RentedCount--;
        PooledCount++;
    }

    /// <summary>Frees every pooled block to the allocator. Rented blocks are untouched.</summary>
    /// <remarks>
    /// If the allocator's free throws, the exception propagates; the blocks freed before it are gone, and the rest,
    /// the failed one included, stay pooled and counted in <see cref="PooledCount"/>.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The pool is disposed.</exception>
    public void Trim()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        FreePooled();
    }

    /// <summary>
    /// Frees every pooled block. Idempotent; afterwards other members throw <see cref="ObjectDisposedException"/>.
    /// </summary>
    /// <remarks>
    /// Rented blocks aren't freed: their renters return them first. A block still rented here stays allocated, and a
    /// tracking allocator reports it as a leak. If the allocator's free throws, the pool stays undisposed with the
    /// unfreed blocks still pooled, as in <see cref="Trim"/>, so a later Dispose frees them.
    /// </remarks>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        FreePooled();
        _disposed = true;
    }

    // The head leaves the list only once its free succeeds, so if the allocator throws, the block it couldn't free
    // is still pooled and counted, and a later Trim or Dispose frees it with the rest.
    private void FreePooled()
    {
        while (_head != null)
        {
            byte* block = _head;

            // Sound: as in Rent, a pooled block's first 8 bytes hold the next pooled block. Read before the free,
            // after which the block is no longer ours to read.
            byte* next = *(byte**)block;
            _allocator.Free(block);
            _head = next;
            PooledCount--;
        }
    }
}
