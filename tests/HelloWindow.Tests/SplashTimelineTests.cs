using System;
using HelloWindow.Splash;
using Xunit;

namespace HelloWindow.Tests;

public sealed class SplashTimelineTests
{
    // A fake monotonic clock in milliseconds, starting well away from zero.
    private const long TicksPerSecond = 1000;
    private const long Start = 50_000;

    [Fact]
    public void Windowed_BeforeMinimum_ShowsSplash()
    {
        var timeline = SplashTimeline.Windowed(TicksPerSecond);

        Assert.True(timeline.ShowsSplash(0, Start));
        Assert.True(timeline.ShowsSplash(1, Start + 1));
        Assert.True(timeline.ShowsSplash(2, Start + 1999));
        Assert.False(timeline.IsComplete);
    }

    [Fact]
    public void Windowed_AtMinimum_ShowsScene()
    {
        var timeline = SplashTimeline.Windowed(TicksPerSecond);

        Assert.True(timeline.ShowsSplash(0, Start));
        Assert.False(timeline.ShowsSplash(1, Start + 2000));
        Assert.True(timeline.IsComplete);
    }

    [Fact]
    public void Windowed_ManyFastFramesBeforeMinimum_StillShowsSplash()
    {
        // Frame counts don't end a windowed splash: thousands of frames in under 2 s are all splash frames.
        var timeline = SplashTimeline.Windowed(TicksPerSecond);
        _ = timeline.ShowsSplash(0, Start);

        Assert.True(timeline.ShowsSplash(10_000, Start + 1999));
    }

    [Fact]
    public void Windowed_ClockStartsAtFirstFrame()
    {
        // The clock's absolute value means nothing: the splash starts when the first frame is decided.
        var timeline = SplashTimeline.Windowed(TicksPerSecond);

        Assert.True(timeline.ShowsSplash(0, Start + 10_000));
        Assert.True(timeline.ShowsSplash(1, Start + 11_999));
        Assert.False(timeline.ShowsSplash(2, Start + 12_000));
    }

    [Fact]
    public void Headless_FirstNFrames_ShowSplash()
    {
        var timeline = SplashTimeline.Headless();

        // Time is irrelevant headless: even an hour per frame doesn't end the splash early.
        for (int frame = 0; frame < SplashTimeline.HeadlessFrames; frame++)
        {
            Assert.True(timeline.ShowsSplash(frame, Start + (frame * 3_600_000L)), $"frame {frame}");
        }

        Assert.False(timeline.IsComplete);
        Assert.False(timeline.ShowsSplash(SplashTimeline.HeadlessFrames, long.MaxValue));
        Assert.True(timeline.IsComplete);
    }

    [Fact]
    public void Headless_FrameCount_IsMinimumAtSixtyFramesPerSecond()
    {
        int minimumFrames = (int)Math.Ceiling(SplashTimeline.MinimumSeconds * SplashTimeline.HeadlessFramesPerSecond);

        Assert.Equal(SplashTimeline.HeadlessFrames, minimumFrames);
    }

    [Fact]
    public void Headless_SameFrameAskedAgain_GivesSameAnswer()
    {
        // A frame that wasn't presented (an out-of-date swapchain) is decided again with the same number.
        var timeline = SplashTimeline.Headless();

        Assert.True(timeline.ShowsSplash(SplashTimeline.HeadlessFrames - 1, Start));
        Assert.True(timeline.ShowsSplash(SplashTimeline.HeadlessFrames - 1, Start));
    }

    [Fact]
    public void ShowsSplash_AfterSceneStarted_NeverReturnsToSplash()
    {
        var timeline = SplashTimeline.Windowed(TicksPerSecond);
        _ = timeline.ShowsSplash(0, Start);
        _ = timeline.ShowsSplash(1, Start + 2000);

        // Same timestamp again (a frame that wasn't presented): still the scene.
        Assert.False(timeline.ShowsSplash(1, Start + 2000));
    }

    [Fact]
    public void ShowsSplash_NegativeFrame_Throws()
    {
        var timeline = SplashTimeline.Headless();

        Assert.Throws<ArgumentOutOfRangeException>(() => timeline.ShowsSplash(-1, Start));
    }

    [Fact]
    public void ShowsSplash_FrameGoesBackwards_Throws()
    {
        var timeline = SplashTimeline.Headless();
        _ = timeline.ShowsSplash(5, Start);

        Assert.Throws<ArgumentOutOfRangeException>(() => timeline.ShowsSplash(4, Start));
    }

    [Fact]
    public void ShowsSplash_ClockGoesBackwards_Throws()
    {
        var timeline = SplashTimeline.Windowed(TicksPerSecond);
        _ = timeline.ShowsSplash(0, Start);

        Assert.Throws<ArgumentOutOfRangeException>(() => timeline.ShowsSplash(1, Start - 1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1000)]
    public void Windowed_NonPositiveFrequency_Throws(long ticksPerSecond)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SplashTimeline.Windowed(ticksPerSecond));
    }
}
