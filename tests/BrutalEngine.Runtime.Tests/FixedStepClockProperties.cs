using System;
using System.Linq;
using CsCheck;
using Xunit;
using Xunit.Sdk;

namespace BrutalEngine.Runtime.Tests;

/// <summary>
/// Property tests of docs/design/runtime.md section 4 against an exact 128-bit model, over random rates, frequencies,
/// limits and timestamp sequences with <see cref="FixedStepClock.Rebase"/> calls mixed in.
/// </summary>
public sealed class FixedStepClockProperties
{
    private const int Iterations = 3_000;

    [Fact]
    public void Steps_NeverDrift()
    {
        FixedSeed.Run(Scenario.Generator, scenario =>
        {
            FixedStepClock clock = scenario.Create();
            Int128 used = 0;
            long stepsAtRebase = 0;
            long previous = scenario.Start;
            foreach ((bool rebase, long timestamp) in scenario.Timestamps())
            {
                if (rebase)
                {
                    clock.Rebase(timestamp);
                    used = 0;
                    stepsAtRebase = clock.StepCount;
                }
                else
                {
                    used += Int128.Min((Int128)timestamp - previous, scenario.MaxTicks);
                    _ = clock.Advance(timestamp);
                }

                Int128 units = used * scenario.Rate;
                Assert.Equal(units / scenario.Frequency, clock.StepCount - stepsAtRebase);
                Assert.Equal(units % scenario.Frequency, clock.Accumulator);
                previous = timestamp;
            }
        });
    }

    [Fact]
    public void SplitDelta_SameSteps()
    {
        Gen<(Scenario Scenario, long Delta, long[] Cuts)> gen =
            from scenario in Scenario.Generator
            from delta in Gen.OneOf(Gen.Long[0, scenario.MaxTicks], Gen.Const(scenario.MaxTicks))
            from cuts in Gen.Long[0, delta].Array[1, 8]
            select (scenario, delta, cuts);

        FixedSeed.Run(gen, sample =>
        {
            (Scenario scenario, long delta, long[] cuts) = sample;
            FixedStepClock whole = scenario.Replay();
            FixedStepClock split = scenario.Replay();
            long end = scenario.End;
            if (end > long.MaxValue - delta)
            {
                return; // no room left after a saturated sequence
            }

            FrameTiming once = whole.Advance(end + delta);
            FrameTiming last = default;
            foreach (long cut in cuts.Append(delta).Order())
            {
                last = split.Advance(end + cut);
                Assert.False(last.Clamped);
            }

            Assert.Equal(whole.StepCount, split.StepCount);
            Assert.Equal(whole.Accumulator, split.Accumulator);
            Assert.Equal(once.Alpha, last.Alpha);
        });
    }

    [Fact]
    public void SameInput_SameOutput()
    {
        FixedSeed.Run(Scenario.Generator, scenario =>
        {
            FixedStepClock first = scenario.Create();
            FixedStepClock second = scenario.Create();
            foreach ((bool rebase, long timestamp) in scenario.Timestamps())
            {
                if (rebase)
                {
                    first.Rebase(timestamp);
                    second.Rebase(timestamp);
                }
                else
                {
                    Assert.Equal(first.Advance(timestamp), second.Advance(timestamp));
                }

                Assert.Equal(
                    (first.FrameCount, first.StepCount, first.SpiralCount, first.Accumulator),
                    (second.FrameCount, second.StepCount, second.SpiralCount, second.Accumulator));
            }
        });
    }

    [Fact]
    public void Alpha_InRange()
    {
        FixedSeed.Run(Scenario.Generator, scenario =>
        {
            FixedStepClock clock = scenario.Create();
            foreach ((bool rebase, long timestamp) in scenario.Timestamps())
            {
                if (rebase)
                {
                    clock.Rebase(timestamp);
                    continue;
                }

                float alpha = clock.Advance(timestamp).Alpha;
                Assert.InRange(alpha, 0f, 0.99999994f);
                Assert.Equal(clock.Accumulator == 0, alpha == 0f);
            }
        });
    }

    [Fact]
    public void SpiralCount_CountsClampedFrames()
    {
        FixedSeed.Run(Scenario.Generator, scenario =>
        {
            FixedStepClock clock = scenario.Create();
            Int128 cap = ((Int128)scenario.MaxTicks * scenario.Rate + scenario.Frequency - 1) / scenario.Frequency;
            long spirals = 0;
            long previous = scenario.Start;
            foreach ((bool rebase, long timestamp) in scenario.Timestamps())
            {
                if (rebase)
                {
                    clock.Rebase(timestamp);
                }
                else
                {
                    Int128 elapsed = (Int128)timestamp - previous;
                    FrameTiming timing = clock.Advance(timestamp);
                    bool clamped = elapsed > scenario.MaxTicks;
                    spirals += clamped ? 1 : 0;
                    Assert.Equal(clamped, timing.Clamped);
                    Assert.Equal(Int128.Min(elapsed, scenario.MaxTicks) * TimeSpan.TicksPerSecond / scenario.Frequency, timing.Delta.Ticks);
                    Assert.InRange(timing.Steps, 0, (int)cap);
                }

                Assert.Equal(spirals, clock.SpiralCount);
                previous = timestamp;
            }
        });
    }

    /// <summary>
    /// One clock configuration and a sequence of frames: each entry is a gap after the previous timestamp (saturating at
    /// <see cref="long.MaxValue"/>) and whether it is a <see cref="FixedStepClock.Rebase"/> instead of an Advance.
    /// </summary>
    private sealed record Scenario(int Rate, long MaxDeltaTicks, long Frequency, long Start, (bool Rebase, long Gap)[] Frames)
    {
        // Generators over-sample the edges: rates 1 and 1000, the TimeSpan and nanosecond frequencies, the largest
        // frequencies the overflow rule accepts, and gaps of zero, exactly maxTicks, one over, almost a step and huge.
        public static readonly Gen<Scenario> Generator =
            from rate in Gen.OneOf(Gen.Const(1), Gen.Const(60), Gen.Const(1000), Gen.Int[1, 1000])
            from frequency in Gen.OneOf(
                Gen.Const(TimeSpan.TicksPerSecond),
                Gen.Const(1_000_000_000L),
                Gen.Long[1, 1_000],
                Gen.Long[1, 1L << 40],
                Gen.Const(long.MaxValue / 1001),
                Gen.Const(long.MaxValue / 2))
            from maxDelta in Gen.OneOf(Gen.Const(2_500_000L), Gen.Const(TimeSpan.TicksPerSecond), Gen.Long[1, TimeSpan.TicksPerSecond])
            where IsValid(rate, maxDelta, frequency)
            let maxTicks = MaxTicksOf(maxDelta, frequency)
            from start in Gen.OneOf(Gen.Const(0L), Gen.Const(long.MinValue), Gen.Long)
            from frames in Gen.Select(
                Gen.Int[0, 7].Select(pick => pick == 0),
                Gen.OneOf(
                    Gen.Const(0L),
                    Gen.Long[0, maxTicks],
                    Gen.Const(maxTicks),
                    Gen.Const(maxTicks + 1),
                    Gen.Const(frequency - 1),
                    Gen.Long[0, long.MaxValue / 4],
                    Gen.Const(long.MaxValue))).Array[1, 40]
            select new Scenario(rate, maxDelta, frequency, start, frames);

        public long MaxTicks => MaxTicksOf(MaxDeltaTicks, Frequency);

        /// <summary>The last timestamp of the sequence.</summary>
        public long End => Timestamps().Select(frame => frame.Timestamp).DefaultIfEmpty(Start).Last();

        public FixedStepClock Create() => FixedStepClockTests.Create(Rate, MaxDeltaTicks, Frequency, Start);

        /// <summary>A clock that has run the whole sequence.</summary>
        public FixedStepClock Replay()
        {
            FixedStepClock clock = Create();
            foreach ((bool rebase, long timestamp) in Timestamps())
            {
                if (rebase)
                {
                    clock.Rebase(timestamp);
                }
                else
                {
                    _ = clock.Advance(timestamp);
                }
            }

            return clock;
        }

        public (bool Rebase, long Timestamp)[] Timestamps()
        {
            var result = new (bool Rebase, long Timestamp)[Frames.Length];
            long timestamp = Start;
            for (int i = 0; i < Frames.Length; i++)
            {
                timestamp = (long)Int128.Min((Int128)timestamp + Frames[i].Gap, long.MaxValue);
                result[i] = (Frames[i].Rebase, timestamp);
            }

            return result;
        }

        public override string ToString() =>
            $"rate {Rate}, max {MaxDeltaTicks} ticks, frequency {Frequency}, start {Start}, frames [{string.Join(", ", Frames.Select(f => (f.Rebase ? "R" : "A") + f.Gap))}]";

        // The note's rules, computed independently in 128 bits: maxTicks rounded down, at least one step, no overflow.
        private static bool IsValid(int rate, long maxDelta, long frequency)
        {
            Int128 units = (Int128)MaxTicksOf(maxDelta, frequency) * rate;
            return units >= frequency && units + frequency <= long.MaxValue;
        }

        private static long MaxTicksOf(long maxDelta, long frequency) => (long)((Int128)maxDelta * frequency / TimeSpan.TicksPerSecond);
    }

    /// <summary>
    /// Runs a property over a fixed sequence of CsCheck samples (one PCG stream per iteration from a constant seed), so
    /// every run checks exactly the same cases and a failure names the case and how to replay it.
    /// </summary>
    private static class FixedSeed
    {
        private const ulong Seed = 0x0357_5EED_C10C_0001;

        public static void Run<T>(Gen<T> gen, Action<T> check)
        {
            for (uint stream = 0; stream < Iterations; stream++)
            {
                T sample = gen.Generate(new PCG(stream, Seed), null, out _);
                try
                {
                    check(sample);
                }
                catch (Exception exception)
                {
                    throw new XunitException($"Sample {stream} (PCG stream {stream}, seed 0x{Seed:X}) failed: {sample}", exception);
                }
            }
        }
    }
}
