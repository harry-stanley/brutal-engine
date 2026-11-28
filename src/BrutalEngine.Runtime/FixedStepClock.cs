using System;

namespace BrutalEngine.Runtime;

/// <summary>
/// The frame loop's time-keeping: turns timestamps into whole fixed steps, an interpolation factor and a clamped frame
/// delta, with no drift however the time is split into frames (docs/design/runtime.md section 4).
/// </summary>
/// <remarks>
/// <para>
/// The clock keeps a baseline timestamp and an accumulator. The accumulator counts timestamp ticks multiplied by
/// <see cref="FrameLoopOptions.FixedUpdateRate"/>, so one fixed step is exactly <c>timestampFrequency</c> units and no
/// rounded step length is ever summed. Over the <see cref="Advance"/> calls since construction or the last
/// <see cref="Rebase"/>, <see cref="StepCount"/> grows by exactly <c>floor(sum(used) * rate / frequency)</c>, where
/// <c>used</c> is each frame's elapsed time clamped to <see cref="FrameLoopOptions.MaxFrameDelta"/>.
/// </para>
/// <para>
/// Timestamps are opaque <see cref="long"/> values from one monotonic source (the host reads
/// <see cref="TimeProvider.GetTimestamp"/>); any value is accepted, including negative ones, as long as each call's
/// timestamp is not before the baseline.
/// </para>
/// <para>
/// Not thread-safe: the frame loop owns the clock. <see cref="Advance"/> and <see cref="Rebase"/> allocate nothing
/// (only their misuse exceptions do).
/// </para>
/// </remarks>
public sealed class FixedStepClock
{
    private const int MinRate = 1;
    private const int MaxRate = 1000;

    // The largest float below 1 (1 - 2^-24): Alpha's upper limit when the exact ratio rounds up to 1 as a float.
    private const float MaxAlpha = 0.99999994f;

    private readonly int _rate;
    private readonly long _frequency;
    private readonly long _maxTicks;
    private long _baseline;
    private long _accumulator;
    private long _frameCount;
    private long _stepCount;
    private long _spiralCount;

    /// <summary>Creates a clock whose baseline is <paramref name="startTimestamp"/>, with an empty accumulator.</summary>
    /// <param name="options">The fixed update rate and the frame-delta limit. The clock copies them.</param>
    /// <param name="timestampFrequency">Timestamp ticks per second (e.g. <see cref="TimeProvider.TimestampFrequency"/>).</param>
    /// <param name="startTimestamp">The first baseline.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="FrameLoopOptions.FixedUpdateRate"/> is outside 1 to 1000; <see cref="FrameLoopOptions.MaxFrameDelta"/>
    /// is longer than 1 s, or shorter than one fixed step in timestamp ticks (<c>maxTicks * rate &lt; frequency</c>,
    /// which includes zero and negative values); <paramref name="timestampFrequency"/> is not positive; or
    /// <c>maxTicks * rate + timestampFrequency</c> would overflow a <see cref="long"/>.
    /// </exception>
    public FixedStepClock(FrameLoopOptions options, long timestampFrequency, long startTimestamp)
    {
        ArgumentNullException.ThrowIfNull(options);
        int rate = options.FixedUpdateRate;
        if (rate is < MinRate or > MaxRate)
        {
            throw new ArgumentOutOfRangeException(nameof(options), rate, "FrameLoopOptions.FixedUpdateRate must be from 1 to 1000 steps per second.");
        }

        if (options.MaxFrameDelta > TimeSpan.FromSeconds(1))
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.MaxFrameDelta, "FrameLoopOptions.MaxFrameDelta must be at most 1 s.");
        }

        if (timestampFrequency <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(timestampFrequency), timestampFrequency, "The timestamp frequency must be positive.");
        }

        // 128-bit, so neither product can overflow. MaxFrameDelta is at most 1 s here, so maxTicks <= frequency fits a
        // long; division truncates, which rounds down for the positive values that pass the next check.
        Int128 maxTicks = (Int128)options.MaxFrameDelta.Ticks * timestampFrequency / TimeSpan.TicksPerSecond;
        Int128 maxUnits = maxTicks * rate;
        if (maxUnits < timestampFrequency)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options), options.MaxFrameDelta, "FrameLoopOptions.MaxFrameDelta is shorter than one fixed step at this timestamp frequency.");
        }

        if (maxUnits + timestampFrequency > long.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timestampFrequency), timestampFrequency, "The timestamp frequency is too high: MaxFrameDelta times the rate would overflow the accumulator.");
        }

        _rate = rate;
        _frequency = timestampFrequency;
        _maxTicks = (long)maxTicks;
        _baseline = startTimestamp;
        FixedDelta = new TimeSpan((TimeSpan.TicksPerSecond + (rate / 2)) / rate);
    }

    /// <summary>The length of one fixed step: <c>1 / FixedUpdateRate</c> seconds, rounded to the nearest 100 ns (halves up).</summary>
    public TimeSpan FixedDelta { get; }

    /// <summary>The number of <see cref="Advance"/> calls so far. <see cref="Rebase"/> doesn't change it.</summary>
    public long FrameCount => _frameCount;

    /// <summary>The total of every <see cref="FrameTiming.Steps"/> returned so far. <see cref="Rebase"/> doesn't change it.</summary>
    public long StepCount => _stepCount;

    /// <summary>
    /// The number of frames whose elapsed time was longer than <see cref="FrameLoopOptions.MaxFrameDelta"/> and was
    /// clamped (spiral-of-death protection). <see cref="Rebase"/> doesn't change it.
    /// </summary>
    public long SpiralCount => _spiralCount;

    /// <summary>The accumulator: timestamp ticks times the rate, less every step taken. Always below the frequency.</summary>
    internal long Accumulator => _accumulator;

    /// <summary>
    /// Measures one frame from the baseline to <paramref name="timestamp"/>, then makes <paramref name="timestamp"/>
    /// the baseline. Call once per frame.
    /// </summary>
    /// <param name="timestamp">The frame's timestamp; not before the baseline.</param>
    /// <returns>
    /// The frame's steps, interpolation factor, clamped delta and whether it was clamped. A call at the baseline's own
    /// timestamp returns 0 steps: no time has passed. Steps never exceed
    /// <c>floor((maxTicks * rate + frequency - 1) / frequency)</c> (15 at the defaults).
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="timestamp"/> is before the baseline. Nothing changes: the baseline, accumulator and counters
    /// keep their values.
    /// </exception>
    /// <remarks>Allocates nothing.</remarks>
    public FrameTiming Advance(long timestamp)
    {
        ThrowIfBeforeBaseline(timestamp);

        // timestamp >= baseline, so the true difference is in [0, 2^64 - 1]: the wrapping subtraction gives it exactly
        // as an unsigned value, even across the whole long range.
        ulong elapsed = unchecked((ulong)(timestamp - _baseline));
        _baseline = timestamp;
        bool clamped = elapsed > (ulong)_maxTicks;
        long used = clamped ? _maxTicks : (long)elapsed;

        // The accumulator holds less than one step (frequency units) before the frame, and used * rate is at most
        // maxTicks * rate, which the constructor checked fits a long together with the frequency.
        _accumulator += used * _rate;
        long steps = _accumulator / _frequency;
        _accumulator -= steps * _frequency;

        // The ratio is below 1, but as a float it rounds up to 1 when the remainder is within 2^-25 of a step.
        float alpha = (float)((double)_accumulator / _frequency);
        if (alpha >= 1f)
        {
            alpha = MaxAlpha;
        }

        // used <= maxTicks <= frequency, so the 128-bit product can't overflow and the quotient (<= 1 s) fits a long.
        TimeSpan delta = new((long)((Int128)used * TimeSpan.TicksPerSecond / _frequency));

        _frameCount++;
        _stepCount += steps;
        if (clamped)
        {
            _spiralCount++;
        }

        // At most maxTicks * rate / frequency + 1 <= rate + 1 steps (maxTicks <= frequency), so the cast can't truncate.
        return new FrameTiming((int)steps, alpha, delta, clamped);
    }

    /// <summary>
    /// Makes <paramref name="timestamp"/> the baseline and empties the accumulator, so the loop resumes without
    /// catching up: the time before <paramref name="timestamp"/> is never simulated. No counter changes.
    /// </summary>
    /// <param name="timestamp">The new baseline; not before the current one.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="timestamp"/> is before the baseline. Nothing changes.
    /// </exception>
    /// <remarks>Allocates nothing.</remarks>
    public void Rebase(long timestamp)
    {
        ThrowIfBeforeBaseline(timestamp);
        _baseline = timestamp;
        _accumulator = 0;
    }

    private void ThrowIfBeforeBaseline(long timestamp)
    {
        if (timestamp < _baseline)
        {
            throw new ArgumentOutOfRangeException(nameof(timestamp), timestamp, "The timestamp is before the clock's baseline.");
        }
    }
}
