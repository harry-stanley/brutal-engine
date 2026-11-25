using BrutalEngine.Core.Logging;
using Xunit;

namespace BrutalEngine.Core.Tests.Logging;

public sealed class LogLevelTests
{
    [Fact]
    public void Markers_MapToTheirLevels()
    {
        Assert.Equal(LogLevel.Trace, LevelOf<TraceLevel>());
        Assert.Equal(LogLevel.Debug, LevelOf<DebugLevel>());
        Assert.Equal(LogLevel.Information, LevelOf<InformationLevel>());
        Assert.Equal(LogLevel.Warning, LevelOf<WarningLevel>());
        Assert.Equal(LogLevel.Error, LevelOf<ErrorLevel>());
        Assert.Equal(LogLevel.Critical, LevelOf<CriticalLevel>());
    }

    [Fact]
    public void DynamicMarker_IsNone()
    {
        Assert.Equal(LogLevel.None, LevelOf<DynamicLevel>());
    }

    [Fact]
    public void Levels_AreOrderedBySeverity()
    {
        Assert.True(LogLevel.Trace < LogLevel.Debug);
        Assert.True(LogLevel.Debug < LogLevel.Information);
        Assert.True(LogLevel.Information < LogLevel.Warning);
        Assert.True(LogLevel.Warning < LogLevel.Error);
        Assert.True(LogLevel.Error < LogLevel.Critical);
        Assert.True(LogLevel.Critical < LogLevel.None);
    }

    private static LogLevel LevelOf<TLevel>()
        where TLevel : struct, ILogLevel => TLevel.Level;
}
