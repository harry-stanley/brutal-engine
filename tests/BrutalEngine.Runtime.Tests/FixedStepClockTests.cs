using System;
using Xunit;

namespace BrutalEngine.Runtime.Tests;

public sealed class FixedStepClockTests
{
    private const long OneSecond = TimeSpan.TicksPerSecond;
    private const long DefaultMax = 2_500_000; // 250 ms in TimeSpan ticks

    [Fact]
    public void Advance_AtBaseline_ZeroSteps()
    {
        FixedStepClock clock = Create(60, DefaultMax, OneSecond, start: 12_345);

        Assert.Equal(new FrameTiming(0, 0f, TimeSpan.Zero, false), clock.Advance(12_345));
        Assert.Equal(1, clock.FrameCount);
        Assert.Equal(0, clock.StepCount);

        // With part of a step accumulated: still no step, the remainder (and so Alpha) stays, and the baseline has
        // moved, so a repeated timestamp measures nothing.
        FrameTiming partial = clock.Advance(12_345 + 100_000);
        Assert.Equal(0, partial.Steps);
        Assert.Equal(new FrameTiming(0, partial.Alpha, TimeSpan.Zero, false), clock.Advance(12_345 + 100_000));
        Assert.Equal(6_000_000, clock.Accumulator);
        Assert.Equal(3, clock.FrameCount);
    }

    // Rate 60 at 600 ticks per second: one step is 10 ticks, and 250 ms is 150 ticks.
    [Theory]
    [InlineData(60, 600, DefaultMax, 0, 10, 1, 0, 166_666, false)] // one step
    [InlineData(60, 600, DefaultMax, 0, 35, 3, 300, 583_333, false)] // several steps and a remainder
    [InlineData(60, 600, DefaultMax, 15, 5, 1, 0, 83_333, false)] // the first frame's remainder completes a step
    [InlineData(60, 600, DefaultMax, 0, 50, 5, 0, 833_333, false)] // exact multiple
    [InlineData(60, 600, DefaultMax, 0, 150, 15, 0, DefaultMax, false)] // exactly MaxFrameDelta: not clamped
    [InlineData(60, 600, DefaultMax, 0, 151, 15, 0, DefaultMax, true)] // one tick over: clamped
    [InlineData(60, OneSecond, DefaultMax, 166_666, OneSecond, 15, 9_999_960, DefaultMax, true)] // defaults' 15-step cap, almost a step carried
    [InlineData(60, 1_000_000_000, DefaultMax, 0, 1_000_000_000, 15, 0, DefaultMax, true)] // the cap at a nanosecond clock
    [InlineData(3, 7, 5_000_000, 0, 4, 1, 2, 4_285_714, true)] // maxTicks = 3.5 rounds down to 3, so 4 is clamped
    public void Advance_Cases(
        int rate, long frequency, long maxDeltaTicks, long first, long second, int steps, long accumulator, long deltaTicks, bool clamped)
    {
        FixedStepClock clock = Create(rate, maxDeltaTicks, frequency, start: 1_000);
        _ = clock.Advance(1_000 + first);

        FrameTiming timing = clock.Advance(1_000 + first + second);

        Assert.Equal(new FrameTiming(steps, (float)((double)accumulator / frequency), new TimeSpan(deltaTicks), clamped), timing);
        Assert.Equal(accumulator, clock.Accumulator);
    }

    [Fact]
    public void Advance_OverMax_ClampedAndCountsSpiral()
    {
        FixedStepClock clock = Create(60, DefaultMax, OneSecond, start: 0);

        Assert.Equal(new FrameTiming(15, 0f, TimeSpan.FromMilliseconds(250), true), clock.Advance(DefaultMax + 1));
        Assert.Equal(1, clock.SpiralCount);

        Assert.False(clock.Advance((2 * DefaultMax) + 1).Clamped); // exactly MaxFrameDelta
        Assert.Equal(1, clock.SpiralCount);

        Assert.Equal(15, clock.Advance(100 * OneSecond).Steps);
        Assert.Equal(2, clock.SpiralCount);
        Assert.Equal(3, clock.FrameCount);
        Assert.Equal(45, clock.StepCount);
    }

    [Theory]
    [InlineData(long.MinValue, long.MaxValue, 15, DefaultMax, true)] // the full range: the difference needs 64 unsigned bits
    [InlineData(long.MinValue, -1, 15, DefaultMax, true)]
    [InlineData(-1_000_000, 1_000_000, 12, 2_000_000, false)] // across zero
    public void Advance_ExtremeTimestamps_MeasuredWithoutOverflow(long start, long timestamp, int steps, long deltaTicks, bool clamped)
    {
        FixedStepClock clock = Create(60, DefaultMax, OneSecond, start);

        FrameTiming timing = clock.Advance(timestamp);

        Assert.Equal((steps, new TimeSpan(deltaTicks), clamped), (timing.Steps, timing.Delta, timing.Clamped));
    }

    [Fact]
    public void Advance_LargestFrequency_DeltaAndAlphaStayExact()
    {
        const long Frequency = long.MaxValue / 2;
        FixedStepClock clock = Create(1, OneSecond, Frequency, start: 0);

        // One second of ticks: Delta needs 128-bit arithmetic (ticks * 10^7 overflows a long).
        Assert.Equal(new FrameTiming(1, 0f, TimeSpan.FromSeconds(1), false), clock.Advance(Frequency));

        // One tick short of a step: the exact ratio rounds to 1.0f as a float, so Alpha takes the largest float below 1.
        FrameTiming almost = clock.Advance((2 * Frequency) - 1);
        Assert.Equal(0, almost.Steps);
        Assert.Equal(0.99999994f, almost.Alpha);
    }

    [Fact]
    public void Advance_AlmostAStep_AlphaBelowOne()
    {
        FixedStepClock clock = Create(1, OneSecond, 1_000_000_000, start: 0);

        FrameTiming timing = clock.Advance(999_999_999);

        Assert.Equal(0, timing.Steps);
        Assert.Equal(0.99999994f, timing.Alpha);
    }

    [Fact]
    public void Rebase_ClearsAccumulator_KeepsCounters()
    {
        FixedStepClock clock = Create(60, DefaultMax, 600, start: 0);
        _ = clock.Advance(15); // 1 step, half a step left
        _ = clock.Advance(215); // clamped to 150 ticks: 15 steps, half a step left
        Assert.Equal((2, 16, 1, 300), (clock.FrameCount, clock.StepCount, clock.SpiralCount, clock.Accumulator));

        clock.Rebase(1_000);
        Assert.Equal((2, 16, 1, 0), (clock.FrameCount, clock.StepCount, clock.SpiralCount, clock.Accumulator));

        // Measured from the new baseline, with nothing carried: half a step is not a step.
        Assert.Equal(new FrameTiming(0, 0.5f, new TimeSpan(83_333), false), clock.Advance(1_005));

        clock.Rebase(1_005); // at the baseline itself
        Assert.Equal(0, clock.Accumulator);
        Assert.Equal((3, 16, 1), (clock.FrameCount, clock.StepCount, clock.SpiralCount));
    }

    [Theory]
    [InlineData(1, 10_000_000)]
    [InlineData(3, 3_333_333)]
    [InlineData(6, 1_666_667)]
    [InlineData(60, 166_667)]
    [InlineData(256, 39_063)] // 39,062.5: halves round up
    [InlineData(1000, 10_000)]
    public void FixedDelta_RoundsToNearestTick(int rate, long ticks)
    {
        Assert.Equal(new TimeSpan(ticks), Create(rate, OneSecond, OneSecond, start: 0).FixedDelta);
    }

    [Theory]
    [InlineData(0, DefaultMax, OneSecond, "options", "FrameLoopOptions.FixedUpdateRate")]
    [InlineData(-1, DefaultMax, OneSecond, "options", "FrameLoopOptions.FixedUpdateRate")]
    [InlineData(int.MinValue, DefaultMax, OneSecond, "options", "FrameLoopOptions.FixedUpdateRate")]
    [InlineData(1001, DefaultMax, OneSecond, "options", "FrameLoopOptions.FixedUpdateRate")]
    [InlineData(int.MaxValue, DefaultMax, OneSecond, "options", "FrameLoopOptions.FixedUpdateRate")]
    [InlineData(60, OneSecond + 1, OneSecond, "options", "FrameLoopOptions.MaxFrameDelta must be at most 1 s")]
    [InlineData(60, long.MaxValue, OneSecond, "options", "FrameLoopOptions.MaxFrameDelta must be at most 1 s")]
    [InlineData(60, 0, OneSecond, "options", "FrameLoopOptions.MaxFrameDelta is shorter than one fixed step")]
    [InlineData(60, -1, OneSecond, "options", "FrameLoopOptions.MaxFrameDelta is shorter than one fixed step")]
    [InlineData(60, long.MinValue, OneSecond, "options", "FrameLoopOptions.MaxFrameDelta is shorter than one fixed step")]
    [InlineData(60, 166_666, OneSecond, "options", "FrameLoopOptions.MaxFrameDelta is shorter than one fixed step")]
    [InlineData(1000, 9_999, OneSecond, "options", "FrameLoopOptions.MaxFrameDelta is shorter than one fixed step")]
    [InlineData(60, DefaultMax, 1, "options", "FrameLoopOptions.MaxFrameDelta is shorter than one fixed step")] // maxTicks 0
    [InlineData(60, DefaultMax, 0, "timestampFrequency", "The timestamp frequency must be positive")]
    [InlineData(60, DefaultMax, -1, "timestampFrequency", "The timestamp frequency must be positive")]
    [InlineData(60, DefaultMax, long.MinValue, "timestampFrequency", "The timestamp frequency must be positive")]
    [InlineData(1, OneSecond, (long.MaxValue / 2) + 1, "timestampFrequency", "The timestamp frequency is too high")]
    [InlineData(1000, OneSecond, (long.MaxValue / 1001) + 1, "timestampFrequency", "The timestamp frequency is too high")]
    [InlineData(60, DefaultMax, long.MaxValue, "timestampFrequency", "The timestamp frequency is too high")]
    [InlineData(3, 5_000_001, 3_689_348_372_020_105_682, "timestampFrequency", "The timestamp frequency is too high")] // one over the exact limit
    public void Constructor_InvalidArguments_Throw(int rate, long maxDeltaTicks, long frequency, string parameter, string message)
    {
        FrameLoopOptions options = new() { FixedUpdateRate = rate, MaxFrameDelta = new TimeSpan(maxDeltaTicks) };

        ArgumentOutOfRangeException exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => new FixedStepClock(options, frequency, startTimestamp: 0));

        Assert.Equal(parameter, exception.ParamName);
        Assert.StartsWith(message, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_NullOptions_Throws()
    {
        Assert.Equal("options", Assert.Throws<ArgumentNullException>(() => new FixedStepClock(null!, OneSecond, 0)).ParamName);
    }

    [Theory]
    [InlineData(1, OneSecond, 1)] // the lowest rate and frequency: maxTicks * rate == frequency, exactly one step
    [InlineData(1000, 10_000, OneSecond)] // the highest rate with exactly one step of MaxFrameDelta (1 ms)
    [InlineData(60, 166_667, OneSecond)] // the shortest MaxFrameDelta at 60 Hz
    [InlineData(60, OneSecond, OneSecond)] // the longest MaxFrameDelta
    [InlineData(1, OneSecond, long.MaxValue / 2)] // the highest frequency at rate 1: maxTicks * rate + frequency == long.MaxValue - 1
    [InlineData(1000, OneSecond, long.MaxValue / 1001)] // the highest frequency at rate 1000
    [InlineData(3, 5_000_001, 3_689_348_372_020_105_681)] // maxTicks * rate + frequency == long.MaxValue exactly
    public void Constructor_Boundaries_Accepted(int rate, long maxDeltaTicks, long frequency)
    {
        FixedStepClock clock = Create(rate, maxDeltaTicks, frequency, start: -5);

        Assert.Equal((0, 0, 0, 0), (clock.FrameCount, clock.StepCount, clock.SpiralCount, clock.Accumulator));
        Assert.Equal(0, clock.Advance(-5).Steps);
        Assert.True(clock.Advance(long.MaxValue).Clamped);
    }

    [Theory]
    [MemberData(nameof(BeforeBaselineCases))]
    public void Advance_BeforeBaseline_Throws(long baseline, long timestamp)
    {
        FixedStepClock clock = PrimedAt(baseline);

        for (int attempt = 0; attempt < 2; attempt++)
        {
            Assert.Equal("timestamp", Assert.Throws<ArgumentOutOfRangeException>(() => clock.Advance(timestamp)).ParamName);
            AssertPrimedStateKept(clock);
        }

        AssertBaselineKept(clock, baseline);
    }

    [Theory]
    [MemberData(nameof(BeforeBaselineCases))]
    public void Rebase_BeforeBaseline_Throws(long baseline, long timestamp)
    {
        FixedStepClock clock = PrimedAt(baseline);

        for (int attempt = 0; attempt < 2; attempt++)
        {
            Assert.Equal("timestamp", Assert.Throws<ArgumentOutOfRangeException>(() => clock.Rebase(timestamp)).ParamName);
            AssertPrimedStateKept(clock);
        }

        AssertBaselineKept(clock, baseline);
    }

    [Fact]
    public void Advance_DoesNotAllocate()
    {
        FixedStepClock clock = Create(60, DefaultMax, 1_000_000_000, start: 0);
        long timestamp = 0;
        long checksum = Run(clock, ref timestamp); // warm-up: every path below runs once outside the window

        long before = GC.GetAllocatedBytesForCurrentThread();
        checksum += Run(clock, ref timestamp);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.True(checksum > 0);
        Assert.Equal(2_000, clock.FrameCount);
        Assert.Equal(500, clock.SpiralCount); // the clamped path ran inside the window too

        // 1,000 Advance calls: zero, partial, clamped and almost-a-step frames, with a Rebase every 100 frames.
        static long Run(FixedStepClock clock, ref long timestamp)
        {
            long sum = 0;
            for (int i = 0; i < 1_000; i++)
            {
                timestamp += (i % 4) switch
                {
                    0 => 0,
                    1 => 7_000_000,
                    2 => 5_000_000_000,
                    _ => 16_666_666,
                };
                FrameTiming timing = clock.Advance(timestamp);
                sum += timing.Steps + (long)(timing.Alpha * 1000) + timing.Delta.Ticks + (timing.Clamped ? 1 : 0);
                if (i % 100 == 99)
                {
                    clock.Rebase(++timestamp);
                }
            }

            return sum;
        }
    }

    public static TheoryData<long, long> BeforeBaselineCases() => new()
    {
        { 100, 99 },
        { 0, -1 },
        { -5, long.MinValue },
        { long.MaxValue, 0 },
        { long.MaxValue, long.MinValue }, // timestamp - baseline wraps to +1
    };

    internal static FixedStepClock Create(int rate, long maxDeltaTicks, long frequency, long start) =>
        new(new FrameLoopOptions { FixedUpdateRate = rate, MaxFrameDelta = new TimeSpan(maxDeltaTicks) }, frequency, start);

    // Rate 60 at 600 ticks per second, baseline at the given value after one frame of 15 ticks: 1 step, half left.
    private static FixedStepClock PrimedAt(long baseline)
    {
        FixedStepClock clock = Create(60, DefaultMax, 600, start: baseline - 15);
        Assert.Equal(1, clock.Advance(baseline).Steps);
        return clock;
    }

    private static void AssertPrimedStateKept(FixedStepClock clock) =>
        Assert.Equal((1, 1, 0, 300), (clock.FrameCount, clock.StepCount, clock.SpiralCount, clock.Accumulator));

    // The baseline didn't move: a frame at it measures nothing and keeps the half step.
    private static void AssertBaselineKept(FixedStepClock clock, long baseline) =>
        Assert.Equal(new FrameTiming(0, 0.5f, TimeSpan.Zero, false), clock.Advance(baseline));
}
