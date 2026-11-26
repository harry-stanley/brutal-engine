using System;

namespace BrutalEngine.Graphics.Backend;

/// <summary>
/// One planned frame, handed to <see cref="IBackendDevice.Execute"/>: the frame slot's command stream after planning,
/// with every handle already resolved to a backend id and every barrier inserted (docs/design/graphics.md, "The backend
/// interface" and section 10).
/// </summary>
/// <remarks>
/// <para>
/// Only the front end creates one, at frame submission. The spans point into memory the front end owns and reuses for
/// later frames: they are valid only during the <see cref="IBackendDevice.Execute"/> call that receives the plan, and a
/// backend that needs anything afterwards copies it. Being a <see langword="ref"/> struct, a plan can't be stored on the
/// heap or captured.
/// </para>
/// <para>
/// Immutable. Read on the device's owner thread only, like every backend call. No member allocates.
/// </para>
/// </remarks>
public readonly ref struct FramePlan
{
    /// <summary>Creates a plan. Only the front end calls it.</summary>
    /// <param name="frameSlot">The frame's slot, from 0 to the device request's frames in flight minus 1.</param>
    /// <param name="frameNumber">The frame's number: the count of frames submitted before it.</param>
    /// <param name="commands">The planned command stream.</param>
    /// <param name="acquired">The swapchain images the frame acquired, in acquire order.</param>
    internal FramePlan(int frameSlot, long frameNumber, ReadOnlySpan<byte> commands, ReadOnlySpan<SwapchainImageUse> acquired)
    {
        FrameSlot = frameSlot;
        FrameNumber = frameNumber;
        Commands = commands;
        Acquired = acquired;
    }

    /// <summary>
    /// The frame's slot, from 0 to the device request's frames in flight minus 1: the slot whose
    /// <see cref="IBackendDevice.BeginFrame"/> preceded this plan, and whose completion <see cref="IBackendDevice.Execute"/>
    /// signals.
    /// </summary>
    public int FrameSlot { get; }

    /// <summary>The frame's number: the count of frames submitted before it, for logs and debug names.</summary>
    public long FrameNumber { get; }

    /// <summary>
    /// The planned command stream: a sequence of records the backend reads with the command reader of the command-stream
    /// types, in order. Empty for an empty frame, which still signals the slot's completion. Valid during
    /// <see cref="IBackendDevice.Execute"/> only.
    /// </summary>
    public ReadOnlySpan<byte> Commands { get; }

    /// <summary>
    /// The swapchain images the frame acquired, in acquire order, which <see cref="IBackendDevice.Execute"/> must wait for
    /// (their acquire signals) and leave presentable; the front end then presents each one. Commands that refer to an
    /// acquired image by position index into this list. Valid during <see cref="IBackendDevice.Execute"/> only.
    /// </summary>
    public ReadOnlySpan<SwapchainImageUse> Acquired { get; }
}
