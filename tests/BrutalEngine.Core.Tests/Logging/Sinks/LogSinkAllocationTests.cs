using System;
using System.IO;
using BrutalEngine.Core.Logging;
using BrutalEngine.Core.Logging.Sinks;
using Xunit;

namespace BrutalEngine.Core.Tests.Logging.Sinks;

public sealed class LogSinkAllocationTests
{
    private const int Iterations = 1000;
    private const int WarmUpIterations = 3;

    [Fact]
    public void ConsoleSinkWrite_DoesNotAllocate()
    {
        // Preallocated streams, rewound before each write, so the streams themselves never allocate.
        using MemoryStream output = new(capacity: 4096);
        using MemoryStream errorOutput = new(capacity: 4096);
        using ConsoleLogSink sink = new(new ConsoleLogSinkOptions { Output = output, ErrorOutput = errorOutput, IncludeCallerInfo = true });
        DateTimeOffset timestamp = new(2026, 11, 25, 12, 0, 0, TimeSpan.FromHours(13));

        for (int i = 0; i < WarmUpIterations; i++)
        {
            WriteEntries(sink, output, errorOutput, timestamp, i);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < Iterations; i++)
        {
            WriteEntries(sink, output, errorOutput, timestamp, i);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.True(output.Position > 0);
        Assert.True(errorOutput.Position > 0);
    }

    private static void WriteEntries(ConsoleLogSink sink, MemoryStream output, MemoryStream errorOutput, DateTimeOffset timestamp, int i)
    {
        output.Position = 0;
        errorOutput.Position = 0;
        LogEntry information = new()
        {
            Level = LogLevel.Information,
            Category = "Allocation",
            Timestamp = timestamp.AddMilliseconds(i),
            ThreadId = 1,
            Message = "Loaded 42 assets in 3.5 ms \u2026"u8,
            CallerFilePath = "/src/BrutalEngine.Assets/AssetLoader.cs",
            CallerLineNumber = i,
        };
        sink.Write(information);

        LogEntry error = new()
        {
            Level = LogLevel.Error,
            Category = "Allocation",
            Timestamp = timestamp,
            Message = "Device lost"u8,
            CallerFilePath = "/src/BrutalEngine.Graphics/Device.cs",
            CallerLineNumber = -i,
        };
        sink.Write(error);
        sink.Flush();
    }
}
