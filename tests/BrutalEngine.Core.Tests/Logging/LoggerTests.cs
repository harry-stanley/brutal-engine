using System;
using BrutalEngine.Core.Logging;
using Xunit;

namespace BrutalEngine.Core.Tests.Logging;

public sealed class LoggerTests
{
    [Fact]
    public void Constructor_InvalidOptions_Throws()
    {
        RecordingSink sink = new();

        _ = Assert.Throws<ArgumentNullException>(() => new Logger(null!));
        _ = Assert.Throws<ArgumentException>(() => new Logger(new LoggerOptions { Sinks = null! }));
        _ = Assert.Throws<ArgumentException>(() => new Logger(new LoggerOptions { Sinks = [sink, null!] }));
        _ = Assert.Throws<ArgumentException>(() => new Logger(new LoggerOptions { Sinks = [sink], TimeProvider = null! }));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new Logger(new LoggerOptions { Sinks = [sink], MinimumLevel = (LogLevel)7 }));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new Logger(new LoggerOptions { Sinks = [sink], MaxMessageBytes = 63 }));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new Logger(new LoggerOptions { Sinks = [sink], MaxMessageBytes = 65537 }));
        using Logger smallest = new(new LoggerOptions { Sinks = [sink], MaxMessageBytes = 64 });
        using Logger largest = new(new LoggerOptions { Sinks = [sink], MaxMessageBytes = 65536 });
    }

    [Fact]
    public void CreateCategory_SameName_ReturnsEqualCategory()
    {
        using Logger logger = CreateLogger(new RecordingSink());

        LogCategory first = logger.CreateCategory("Render.Vulkan");
        LogCategory second = logger.CreateCategory("Render.Vulkan");
        LogCategory other = logger.CreateCategory("render.vulkan");

        Assert.Equal(first, second);
        Assert.True(first == second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.Equal("Render.Vulkan", first.Name);
        Assert.NotEqual(first, other);
        Assert.True(first != other);
    }

    [Fact]
    public void CreateCategory_SameNameOnAnotherLogger_ReturnsDifferentCategory()
    {
        using Logger a = CreateLogger(new RecordingSink());
        using Logger b = CreateLogger(new RecordingSink());

        Assert.NotEqual(a.CreateCategory("Audio"), b.CreateCategory("Audio"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("slash/name")]
    [InlineData("caf\u00e9")]
    [InlineData("tab\t")]
    [InlineData("A1234567890123456789012345678901234567890123456789012345678901234")]
    public void CreateCategory_InvalidName_Throws(string name)
    {
        using Logger logger = CreateLogger(new RecordingSink());

        _ = Assert.Throws<ArgumentException>(() => logger.CreateCategory(name));
        _ = Assert.Throws<ArgumentNullException>(() => logger.CreateCategory(null!));
    }

    [Theory]
    [InlineData("a")]
    [InlineData("Render.Vulkan_2-x")]
    [InlineData("A123456789012345678901234567890123456789012345678901234567890123")]
    public void CreateCategory_ValidName_Succeeds(string name)
    {
        using Logger logger = CreateLogger(new RecordingSink());

        Assert.Equal(name, logger.CreateCategory(name).Name);
    }

    [Fact]
    public void CreateCategory_BeyondCapacity_Throws()
    {
        using Logger logger = CreateLogger(new RecordingSink());
        for (int i = 0; i < 1024; i++)
        {
            _ = logger.CreateCategory($"C{i}");
        }

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => logger.CreateCategory("OneTooMany"));

        Assert.Contains("1024", exception.Message, StringComparison.Ordinal);
        Assert.Equal("C7", logger.CreateCategory("C7").Name);
    }

    [Fact]
    public void SetLevel_OverridesMinimumForThatCategoryOnly()
    {
        using Logger logger = CreateLogger(new RecordingSink(), LogLevel.Warning);
        LogCategory verbose = logger.CreateCategory("Verbose");
        LogCategory normal = logger.CreateCategory("Normal");

        logger.SetLevel(verbose, LogLevel.Trace);

        Assert.True(logger.IsEnabled(verbose, LogLevel.Trace));
        Assert.True(verbose.IsEnabled(LogLevel.Debug));
        Assert.False(logger.IsEnabled(normal, LogLevel.Information));
        Assert.True(normal.IsEnabled(LogLevel.Warning));
        Assert.True(logger.IsEnabled(logger.CreateCategory("Verbose"), LogLevel.Trace));

        logger.SetLevel(normal, LogLevel.None);
        Assert.False(normal.IsEnabled(LogLevel.Critical));
    }

    [Fact]
    public void IsEnabled_FollowsMinimumLevel()
    {
        using Logger logger = CreateLogger(new RecordingSink(), LogLevel.Information);
        LogCategory category = logger.CreateCategory("Core");

        Assert.False(category.IsEnabled(LogLevel.Trace));
        Assert.False(category.IsEnabled(LogLevel.Debug));
        Assert.True(category.IsEnabled(LogLevel.Information));
        Assert.True(category.IsEnabled(LogLevel.Critical));
        Assert.False(category.IsEnabled(LogLevel.None));
        Assert.False(category.IsEnabled((LogLevel)200));
    }

    [Fact]
    public void SetLevel_InvalidArguments_Throw()
    {
        using Logger logger = CreateLogger(new RecordingSink());
        using Logger other = CreateLogger(new RecordingSink());
        LogCategory category = logger.CreateCategory("Core");

        _ = Assert.Throws<ArgumentOutOfRangeException>(() => logger.SetLevel(category, (LogLevel)7));
        _ = Assert.Throws<ArgumentException>(() => logger.SetLevel(default, LogLevel.Trace));
        _ = Assert.Throws<ArgumentException>(() => other.SetLevel(category, LogLevel.Trace));
        _ = Assert.Throws<ArgumentException>(() => other.IsEnabled(category, LogLevel.Trace));
        _ = Assert.Throws<ArgumentException>(() => logger.IsEnabled(default, LogLevel.Trace));
    }

    [Fact]
    public void Flush_FlushesEverySinkAndCountsFailures()
    {
        RecordingSink failing = new() { ThrowOnFlush = true };
        RecordingSink healthy = new();
        using Logger logger = CreateLogger(failing, healthy);

        logger.Flush();

        Assert.Equal(1, failing.FlushCount);
        Assert.Equal(1, healthy.FlushCount);
        Assert.Equal(1, logger.SinkFailureCount);
    }

    [Fact]
    public void Dispose_FlushesSinks()
    {
        RecordingSink first = new();
        RecordingSink second = new();
        Logger logger = CreateLogger(first, second);

        logger.Dispose();
        logger.Dispose();

        Assert.Equal(1, first.FlushCount);
        Assert.Equal(1, second.FlushCount);
    }

    [Fact]
    public void MembersAfterDispose_ThrowObjectDisposed()
    {
        Logger logger = CreateLogger(new RecordingSink());
        LogCategory category = logger.CreateCategory("Core");
        logger.Dispose();

        _ = Assert.Throws<ObjectDisposedException>(() => logger.CreateCategory("Core"));
        _ = Assert.Throws<ObjectDisposedException>(() => logger.SetLevel(category, LogLevel.Trace));
        _ = Assert.Throws<ObjectDisposedException>(() => logger.IsEnabled(category, LogLevel.Error));
        _ = Assert.Throws<ObjectDisposedException>(() => logger.SinkFailureCount);
        _ = Assert.Throws<ObjectDisposedException>(logger.Flush);
        Assert.False(category.IsEnabled(LogLevel.Critical));
        Assert.Equal("Core", category.Name);
    }

    private static Logger CreateLogger(RecordingSink sink, LogLevel minimumLevel = LogLevel.Information) =>
        new(new LoggerOptions { Sinks = [sink], MinimumLevel = minimumLevel });

    private static Logger CreateLogger(params RecordingSink[] sinks) =>
        new(new LoggerOptions { Sinks = sinks });
}
