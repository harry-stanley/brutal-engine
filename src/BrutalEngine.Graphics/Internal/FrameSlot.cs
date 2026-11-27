using System;
using BrutalEngine.Core.Memory;
using BrutalEngine.Graphics.Recording;

namespace BrutalEngine.Graphics.Internal;

/// <summary>
/// One of the device's frames in flight (docs/design/graphics.md section 4): its command stream and the number of the
/// frame last submitted from it.
/// </summary>
/// <remarks>Owned by the device, which disposes it. Not thread-safe: the device's owner thread only.</remarks>
internal sealed class FrameSlot : IDisposable
{
    /// <summary>Creates a slot whose memory comes from <paramref name="allocator"/>.</summary>
    /// <param name="allocator">Source of the slot's unmanaged memory. Not owned; must outlive the slot.</param>
    /// <exception cref="OutOfMemoryException">The allocator failed; nothing stays allocated.</exception>
    internal FrameSlot(INativeAllocator allocator) => Commands = new CommandWriter(allocator);

    /// <summary>The command stream the open frame records into; reset when the slot begins a frame.</summary>
    internal CommandWriter Commands { get; }

    /// <summary>The number of the frame last submitted from the slot; -1 before the first.</summary>
    internal long LastSubmittedFrame { get; set; } = -1;

    /// <summary>Frees the slot's memory.</summary>
    public void Dispose() => Commands.Dispose();
}
