using System;
using BrutalEngine.Graphics.Backend;

namespace BrutalEngine.TestUtilities;

/// <summary>
/// A copy of one <see cref="FramePlan"/> a <see cref="FakeBackendDevice"/> executed while
/// <see cref="FakeBackendScript.RecordPlans"/> was on, kept after the plan's spans are gone.
/// </summary>
/// <remarks>
/// Immutable: the device copies the plan's command bytes and acquired images into new arrays, which allocates (test
/// infrastructure, not engine code). The command stream is kept as bytes; decoding it waits for the command reader (#230).
/// </remarks>
public sealed class RecordedPlan
{
    private readonly byte[] _commands;
    private readonly SwapchainImageUse[] _acquired;

    internal RecordedPlan(in FramePlan plan)
    {
        FrameSlot = plan.FrameSlot;
        FrameNumber = plan.FrameNumber;
        _commands = plan.Commands.ToArray();
        _acquired = plan.Acquired.ToArray();
    }

    /// <summary>The plan's frame slot.</summary>
    public int FrameSlot { get; }

    /// <summary>The plan's frame number.</summary>
    public long FrameNumber { get; }

    /// <summary>A copy of the plan's command stream.</summary>
    public ReadOnlySpan<byte> Commands => _commands;

    /// <summary>A copy of the images the plan waited for and left presentable.</summary>
    public ReadOnlySpan<SwapchainImageUse> Acquired => _acquired;
}
