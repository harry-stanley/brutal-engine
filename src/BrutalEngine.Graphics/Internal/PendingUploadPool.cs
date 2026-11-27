using System.Collections.Generic;
using BrutalEngine.Graphics.Backend;

namespace BrutalEngine.Graphics.Internal;

/// <summary>
/// The pending upload blocks: staging buffers for writes made between frames, which can't use a frame slot's ring because
/// it may still be in flight (docs/design/graphics.md section 6). Blocks are reused: the next frame takes the pending ones
/// at <c>BeginFrame</c> and gives them back once it has completed.
/// </summary>
/// <remarks>
/// Every block has the ring's size. The lists grow only when the pool does, so a warm pool allocates nothing. Not
/// thread-safe: the device's owner thread only.
/// </remarks>
internal sealed class PendingUploadPool
{
    private readonly List<UploadRing> _pending = [];
    private readonly List<UploadRing> _free = [];

    /// <summary>Reserves room for a write in the pending blocks, taking a free block when the current one is full.</summary>
    /// <param name="size">The bytes needed; at most a block's size.</param>
    /// <param name="offset">The reserved offset in the returned block.</param>
    /// <returns>The block, or <see langword="null"/> when no pending or free block has room: the caller adds a new one.</returns>
    internal UploadRing? TryReserve(long size, out long offset)
    {
        offset = _pending.Count > 0 ? _pending[^1].TryReserve(size) : -1;
        if (offset < 0 && _free.Count > 0)
        {
            _pending.Add(_free[^1]);
            _free.RemoveAt(_free.Count - 1);
            offset = _pending[^1].TryReserve(size);
        }

        return offset < 0 ? null : _pending[^1];
    }

    /// <summary>Adds a new block to the pending ones; the caller reserves from it next.</summary>
    /// <param name="block">A block the device just created.</param>
    internal void Add(UploadRing block) => _pending.Add(block);

    /// <summary>Moves every pending block to a frame slot, whose frame copies from them.</summary>
    /// <param name="slotBlocks">The slot's list of blocks it returns on completion; empty.</param>
    internal void HandOver(List<UploadRing> slotBlocks)
    {
        slotBlocks.AddRange(_pending);
        _pending.Clear();
    }

    /// <summary>Takes back the blocks of a frame that has completed, emptied.</summary>
    /// <param name="slotBlocks">The slot's list; cleared.</param>
    internal void Return(List<UploadRing> slotBlocks)
    {
        foreach (UploadRing block in slotBlocks)
        {
            block.Reset();
            _free.Add(block);
        }

        slotBlocks.Clear();
    }

    /// <summary>Destroys the pending and free blocks' buffers. Blocks held by frame slots are the slots' to destroy.</summary>
    /// <param name="backend">The device's backend device.</param>
    internal void Destroy(IBackendDevice backend)
    {
        Destroy(backend, _pending);
        Destroy(backend, _free);
    }

    /// <summary>Destroys the buffers of a list of blocks and empties it.</summary>
    /// <param name="backend">The device's backend device.</param>
    /// <param name="blocks">The blocks.</param>
    internal static void Destroy(IBackendDevice backend, List<UploadRing> blocks)
    {
        foreach (UploadRing block in blocks)
        {
            backend.Destroy(BackendObjectKind.Buffer, block.Id);
        }

        blocks.Clear();
    }
}
