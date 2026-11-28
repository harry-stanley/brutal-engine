using System;

namespace BrutalEngine.Runtime;

/// <summary>Settings for the frame loop's fixed-step clock. Validated by the <see cref="FixedStepClock"/> constructor.</summary>
/// <remarks>
/// An immutable record: the clock copies what it needs when it is constructed, so a later <c>with</c> copy has no
/// effect on it. Safe to share between threads. Creating one allocates; reading it doesn't.
/// </remarks>
public sealed record FrameLoopOptions
{
    /// <summary>Fixed simulation steps per second. Default 60; valid from 1 to 1000.</summary>
    public int FixedUpdateRate { get; init; } = 60;

    /// <summary>
    /// The longest frame delta the clock accumulates; a longer frame is clamped to it, which caps the fixed steps one
    /// frame can run (spiral-of-death protection). Default 250 ms; valid from one fixed step (measured in timestamp
    /// ticks at the clock's frequency) to 1 s.
    /// </summary>
    public TimeSpan MaxFrameDelta { get; init; } = TimeSpan.FromMilliseconds(250);
}
