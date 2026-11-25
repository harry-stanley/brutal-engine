using BrutalEngine.Core.Logging;
using Xunit;

namespace BrutalEngine.Core.Tests.Logging;

public sealed class LogCategoryTests
{
    [Fact]
    public void Default_IsDisabledAndUnnamed()
    {
        LogCategory category = default;

        Assert.Equal(string.Empty, category.Name);
        Assert.False(category.IsEnabled(LogLevel.Critical));
        Assert.Equal(default, category);
        Assert.Equal(0, category.GetHashCode());
    }

    [Fact]
    public void Equals_Object_ComparesCategories()
    {
        using Logger logger = new(new LoggerOptions { Sinks = [] });
        LogCategory category = logger.CreateCategory("Core");

        Assert.True(category.Equals((object)logger.CreateCategory("Core")));
        Assert.False(category.Equals((object)default(LogCategory)));
        Assert.False(category.Equals("Core"));
    }
}
