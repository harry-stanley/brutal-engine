using System;
using BrutalEngine.Core.Logging;
using Xunit;

namespace BrutalEngine.Core.Tests.Logging;

public sealed class LogLevelValidationTests
{
    [Fact]
    public void SetLevel_EveryByteValue_AcceptsExactlyTheDefinedLevels()
    {
        // Logger.SetLevel validates with a range check instead of Enum.IsDefined (which can allocate). The range
        // check is only right while LogLevel's members are contiguous from 0 to None, so compare it against
        // Enum.IsDefined for every value the underlying byte can hold.
        using Logger logger = new(new LoggerOptions { Sinks = [] });
        LogCategory category = logger.CreateCategory("Validation");
        int accepted = 0;

        for (int value = byte.MinValue; value <= byte.MaxValue; value++)
        {
            var level = (LogLevel)value;
            if (Enum.IsDefined(level))
            {
                logger.SetLevel(category, level);
                Assert.Equal(level, category.State!.Level);
                accepted++;
            }
            else
            {
                ArgumentOutOfRangeException exception =
                    Assert.Throws<ArgumentOutOfRangeException>(() => logger.SetLevel(category, level));
                Assert.Equal("level", exception.ParamName);
            }
        }

        Assert.Equal(Enum.GetValues<LogLevel>().Length, accepted);
    }
}
