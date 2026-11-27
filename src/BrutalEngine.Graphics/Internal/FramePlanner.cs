using System;
using BrutalEngine.Graphics.Backend;
using BrutalEngine.Graphics.Recording;

namespace BrutalEngine.Graphics.Internal;

/// <summary>Turns a frame slot's recorded commands into the plan the backend executes (docs/design/graphics.md section 10).</summary>
/// <remarks>Pure and allocation-free: the plan's spans point into the slot's own memory.</remarks>
internal static class FramePlanner
{
    /// <summary>Plans one frame.</summary>
    /// <param name="frameSlot">The frame's slot.</param>
    /// <param name="frameNumber">The frame's number: the frames submitted before it.</param>
    /// <param name="commands">The slot's recorded command stream.</param>
    /// <returns>The plan, valid until the slot's stream changes.</returns>
    internal static FramePlan Plan(int frameSlot, long frameNumber, CommandWriter commands)
    {
        // TODO(#332): insert Barrier records from the frame's resource usage; until then the plan is the recorded stream.
        return new FramePlan(frameSlot, frameNumber, commands.Written, ReadOnlySpan<SwapchainImageUse>.Empty);
    }
}
