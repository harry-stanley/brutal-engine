using System;

namespace BrutalEngine.Runtime;

/// <summary>What one <see cref="FixedStepClock.Advance"/> call measured: the work and interpolation for one frame.</summary>
/// <param name="Steps">
/// The whole fixed steps to simulate this frame, each <see cref="FixedStepClock.FixedDelta"/> long. Zero or more, and
/// at most the cap that <see cref="FrameLoopOptions.MaxFrameDelta"/> sets.
/// </param>
/// <param name="Alpha">
/// The interpolation factor between the last two fixed states: the part of a step left in the accumulator, in
/// <c>[0, 1)</c>.
/// </param>
/// <param name="Delta">
/// The time this frame accumulated: the elapsed time since the previous baseline, clamped to
/// <see cref="FrameLoopOptions.MaxFrameDelta"/>, converted from timestamp ticks and rounded down to 100 ns.
/// </param>
/// <param name="Clamped">
/// <see langword="true"/> when the elapsed time was longer than <see cref="FrameLoopOptions.MaxFrameDelta"/> and was
/// clamped; such a frame counts in <see cref="FixedStepClock.SpiralCount"/>.
/// </param>
/// <remarks>Plain immutable data: safe to share between threads, and copying or reading it allocates nothing.</remarks>
public readonly record struct FrameTiming(int Steps, float Alpha, TimeSpan Delta, bool Clamped);
