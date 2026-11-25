using System;
using System.IO;
using System.Text;
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

    [Fact]
    public void MemorySinkWrite_DoesNotAllocate()
    {
        // Small capacities, so the measured writes keep overwriting, truncating and wrapping the rings.
        MemoryLogSink sink = new(capacityEntries: 16, capacityBytes: 256);
        var records = new MemoryLogRecord[16];
        byte[] buffer = new byte[256];
        byte[] longMessage = Encoding.UTF8.GetBytes(new string('\u00e9', 200));

        for (int i = 0; i < WarmUpIterations; i++)
        {
            WriteMemory(sink, records, buffer, longMessage, i);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < Iterations; i++)
        {
            WriteMemory(sink, records, buffer, longMessage, i);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.True(sink.DroppedCount > Iterations);
    }

    private static void WriteMemory(MemoryLogSink sink, MemoryLogRecord[] records, byte[] buffer, byte[] longMessage, int i)
    {
        LogEntry entry = new()
        {
            Level = LogLevel.Warning,
            Category = "Allocation",
            Timestamp = DateTimeOffset.UnixEpoch.AddTicks(i),
            ThreadId = 1,
            Message = "Frame took 18.2 ms \u2026"u8,
            CallerFilePath = "/src/BrutalEngine.Runtime/FrameLoop.cs",
            CallerLineNumber = i,
        };
        sink.Write(entry);

        LogEntry oversized = new() { Level = LogLevel.Error, Category = "Allocation", Message = longMessage };
        sink.Write(oversized);
        sink.Flush();
        _ = sink.CopyTo(records, buffer);
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
