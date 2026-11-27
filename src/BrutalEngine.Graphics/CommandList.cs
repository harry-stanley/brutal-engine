using System;
using BrutalEngine.Core;

namespace BrutalEngine.Graphics;

/// <summary>
/// The recorder of a device's open frame (docs/design/graphics.md sections 4 and 9): <see cref="GraphicsDevice.BeginFrame"/>
/// hands one out, and <see cref="GraphicsDevice.SubmitFrame"/> takes it back.
/// </summary>
/// <remarks>
/// A small value tied to one device and one frame number. Every copy is valid only while that frame is open: after the
/// frame was submitted, or on another device, using it throws <see cref="InvalidOperationException"/>.
/// <c>default(CommandList)</c> belongs to no frame. Owner thread of its device. Copying allocates nothing.
/// </remarks>
public readonly struct CommandList
{
    internal CommandList(GraphicsDevice device, long frameNumber)
    {
        Device = device;
        FrameNumber = frameNumber;
    }

    /// <summary>The device whose frame this is; <see langword="null"/> for <c>default</c>.</summary>
    internal GraphicsDevice? Device { get; }

    /// <summary>The frame's number on its device.</summary>
    internal long FrameNumber { get; }

    /// <summary>Allocates per-frame ring memory that the CPU writes and this frame's GPU work reads without a copy.</summary>
    /// <param name="byteCount">The bytes needed: from 1 to <see cref="int.MaxValue"/>.</param>
    /// <param name="usage">What the memory will be bound as: one or more <see cref="TransientUsage"/> flags.</param>
    /// <param name="allocation">
    /// The memory, its alias buffer handle and its offset, valid until <see cref="GraphicsDevice.SubmitFrame"/>;
    /// <c>default</c> on failure.
    /// </param>
    /// <returns>
    /// Success once the memory is reserved. Fails with <see cref="ErrorKind.Unavailable"/> on a lost device, or with the
    /// backend's failure (<see cref="ErrorKind.OutOfMemory"/>) when the ring or a dedicated block can't be created; then
    /// nothing is reserved or counted.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// The list isn't its device's open frame: <c>default</c>, or a copy from a submitted frame. Or every buffer handle
    /// slot is live or retired (the device's buffer handle allocator is exhausted), so no alias handle can be issued;
    /// the memory stays unused.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="byteCount"/> is below 1 or above <see cref="int.MaxValue"/>, or <paramref name="usage"/> has a flag
    /// that isn't a <see cref="TransientUsage"/> member.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="usage"/> has no flag.</exception>
    /// <exception cref="OutOfMemoryException">The device's handle tables couldn't grow; the memory stays unused.</exception>
    /// <exception cref="ObjectDisposedException">The device has been disposed.</exception>
    /// <exception cref="WrongThreadException">Called from a thread other than the device's owner thread.</exception>
    /// <remarks>
    /// <para>
    /// The memory comes from the frame slot's upload ring, after the frame's writes and earlier allocations, at the next
    /// offset aligned for <paramref name="usage"/> (see <see cref="TransientAllocation.Offset"/>; several flags take the
    /// largest of their alignments). An allocation that doesn't fit gets a dedicated block for this frame only, destroyed
    /// once the frame completes, as for <see cref="GraphicsDevice.WriteBuffer"/>: counted in
    /// <see cref="GraphicsStatistics.UploadOverflowBytes"/> once the allocation has succeeded, and logged at
    /// <see cref="Core.Logging.LogLevel.Warning"/> at most once per frame. Transient memory isn't a copy, so
    /// <see cref="GraphicsStatistics.UploadBytesLastFrame"/> doesn't count it.
    /// </para>
    /// <para>
    /// Owner thread. Allocates no managed memory once the slot's ring exists and the device has made as many alias handles
    /// in an earlier frame, except the exception; making the ring or a dedicated block allocates.
    /// </para>
    /// </remarks>
    public Result AllocateTransient(long byteCount, TransientUsage usage, out TransientAllocation allocation)
    {
        if (Device is null)
        {
            throw new InvalidOperationException(
                "CommandList.AllocateTransient: the command list is default and belongs to no frame. Use the one BeginFrame returned.");
        }

        return Device.AllocateTransient(this, byteCount, usage, out allocation);
    }
}
