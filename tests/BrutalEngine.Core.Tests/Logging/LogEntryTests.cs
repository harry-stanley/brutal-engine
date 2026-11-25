using System;
using BrutalEngine.Core.Logging;
using Xunit;

namespace BrutalEngine.Core.Tests.Logging;

public sealed class LogEntryTests
{
    [Fact]
    public void DefaultEntry_HasEmptyStringsAndMessage()
    {
        LogEntry entry = default;

        Assert.Equal(string.Empty, entry.Category);
        Assert.Equal(string.Empty, entry.CallerFilePath);
        Assert.True(entry.Message.IsEmpty);
    }

    [Fact]
    public void Entry_InitSetsEveryField()
    {
        DateTimeOffset time = new(2026, 11, 25, 1, 2, 3, TimeSpan.Zero);
        LogEntry entry = new()
        {
            Level = LogLevel.Warning,
            Category = "Render",
            Timestamp = time,
            ThreadId = 7,
            Message = "hi"u8,
            CallerFilePath = "a.cs",
            CallerLineNumber = 12,
        };

        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal("Render", entry.Category);
        Assert.Equal(time, entry.Timestamp);
        Assert.Equal(7, entry.ThreadId);
        Assert.True(entry.Message.SequenceEqual("hi"u8));
        Assert.Equal("a.cs", entry.CallerFilePath);
        Assert.Equal(12, entry.CallerLineNumber);
    }
}
