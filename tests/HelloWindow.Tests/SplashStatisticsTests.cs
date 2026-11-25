using HelloWindow.Splash;
using Xunit;

namespace HelloWindow.Tests;

public sealed class SplashStatisticsTests
{
    private const long TicksPerSecond = 1000;

    [Fact]
    public void Format_SplashThenScene_CountsBothAndTimesSplashToFirstSceneFrame()
    {
        var statistics = new SplashStatistics();
        statistics.Presented(splash: true, 10_000);
        statistics.Presented(splash: true, 11_000);
        statistics.Presented(splash: false, 12_250);
        statistics.Presented(splash: false, 20_000);

        Assert.Equal("splash: 2 frames over 2.25 s, scene: 2 frames", statistics.Format(TicksPerSecond));
    }

    [Fact]
    public void SplashSeconds_SceneNeverStarted_EndsAtLastSplashFrame()
    {
        var statistics = new SplashStatistics();
        statistics.Presented(splash: true, 10_000);
        statistics.Presented(splash: true, 11_500);

        Assert.Equal(1.5, statistics.SplashSeconds(TicksPerSecond));
        Assert.Equal(0, statistics.SceneFrames);
    }

    [Fact]
    public void Format_NothingPresented_IsAllZero()
    {
        Assert.Equal("splash: 0 frames over 0.00 s, scene: 0 frames", new SplashStatistics().Format(TicksPerSecond));
    }

    [Fact]
    public void Presented_Counts_EachKindSeparately()
    {
        var statistics = new SplashStatistics();
        for (int i = 0; i < 120; i++)
        {
            statistics.Presented(splash: true, i);
        }

        statistics.Presented(splash: false, 200);

        Assert.Equal(120, statistics.SplashFrames);
        Assert.Equal(1, statistics.SceneFrames);
    }
}
