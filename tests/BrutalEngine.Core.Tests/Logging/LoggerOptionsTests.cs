using System;
using BrutalEngine.Core.Logging;
using Xunit;

namespace BrutalEngine.Core.Tests.Logging;

public sealed class LoggerOptionsTests
{
    [Fact]
    public void Defaults_MatchTheDesign()
    {
        LoggerOptions options = new() { Sinks = [] };

        Assert.Equal(LogLevel.Information, options.MinimumLevel);
        Assert.Same(TimeProvider.System, options.TimeProvider);
        Assert.Equal(4096, options.MaxMessageBytes);
        Assert.Empty(options.Sinks);
    }
}
