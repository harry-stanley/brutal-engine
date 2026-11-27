using System;
using BrutalEngine.Core.Memory;
using BrutalEngine.Graphics.Backend;
using BrutalEngine.Graphics.Recording;

namespace BrutalEngine.Graphics.Internal;

/// <summary>
/// Turns a frame slot's upload copies and recorded commands into the plan the backend executes (docs/design/graphics.md
/// sections 6 and 10): the upload copies first, in call order, then the recorded commands.
/// </summary>
/// <remarks>
/// When only one of the two streams has records, the plan points at it directly. When both do, they are copied into the
/// planner's block, which comes from the allocator given to the constructor, grows only when a plan doesn't fit, and is
/// freed by <see cref="Dispose"/>. The plan is valid until the next <see cref="Plan"/>. Not thread-safe: the device's
/// owner thread only. Allocates no managed memory.
/// </remarks>
/// <param name="allocator">Source of the planner's block. Not owned; must outlive the planner.</param>
internal sealed unsafe class FramePlanner(INativeAllocator allocator) : IDisposable
{
    private byte* _block;
    private int _capacity;

    /// <summary>Plans one frame.</summary>
    /// <param name="frameSlot">The frame's slot.</param>
    /// <param name="frameNumber">The frame's number: the frames submitted before it.</param>
    /// <param name="uploads">The slot's upload copies.</param>
    /// <param name="commands">The slot's recorded command stream.</param>
    /// <returns>The plan, valid until the next call or until either stream changes.</returns>
    /// <exception cref="OutOfMemoryException">The planner's block couldn't grow.</exception>
    internal FramePlan Plan(int frameSlot, long frameNumber, CommandWriter uploads, CommandWriter commands)
    {
        // TODO(#332): insert Barrier records from the frame's resource usage; until then the plan is the copies, then the
        // recorded stream.
        ReadOnlySpan<byte> first = uploads.Written;
        ReadOnlySpan<byte> second = commands.Written;
        ReadOnlySpan<byte> stream = first.IsEmpty ? second : second.IsEmpty ? first : Concatenate(first, second);
        return new FramePlan(frameSlot, frameNumber, stream, ReadOnlySpan<SwapchainImageUse>.Empty);
    }

    /// <summary>Frees the planner's block. Later calls do nothing.</summary>
    public void Dispose()
    {
        if (_block != null)
        {
            allocator.Free(_block);
            _block = null;
            _capacity = 0;
        }
    }

    // Both streams are whole records of 8-byte multiples, so the joined stream is records too, 8-byte aligned in the block.
    private ReadOnlySpan<byte> Concatenate(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
    {
        int length = checked(first.Length + second.Length);
        if (length > _capacity)
        {
            byte* block = (byte*)allocator.Allocate((nuint)length, 8);
            if (_block != null)
            {
                allocator.Free(_block);
            }

            _block = block;
            _capacity = length;
        }

        // Sound: the block holds _capacity >= length bytes, and the streams live in other blocks.
        var joined = new Span<byte>(_block, length);
        first.CopyTo(joined);
        second.CopyTo(joined[first.Length..]);
        return joined;
    }
}
