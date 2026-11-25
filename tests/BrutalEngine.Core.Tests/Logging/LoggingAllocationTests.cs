using System;
using BrutalEngine.Core.Logging;
using Xunit;

namespace BrutalEngine.Core.Tests.Logging;

public sealed class LoggingAllocationTests
{
    private const int Iterations = 1000;
    private const int WarmUpIterations = 3;

    [Fact]
    public void Log_EnabledCommonTypes_DoesNotAllocate()
    {
        CountingSink sink = new();
        using Logger logger = new(new LoggerOptions { Sinks = [sink], MinimumLevel = LogLevel.Trace });
        LogCategory category = logger.CreateCategory("Allocation");
        string longText = new('a', 5000);

        // Warm up so the pooled buffers exist and one-time JIT work is done before measuring. Use non-zero,
        // negative and fractional values: the BCL lazily caches UTF-8 copies of number-format strings (such as
        // the decimal separator and negative sign) the first time each is needed, once per process.
        for (int i = 1; i <= WarmUpIterations; i++)
        {
            LogCommonTypes(category, i, longText);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 1; i <= Iterations; i++)
        {
            LogCommonTypes(category, i, longText);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.Equal((WarmUpIterations + Iterations) * 4, sink.WriteCount);
    }

    [Fact]
    public void Log_Disabled_DoesNotAllocate()
    {
        CountingSink sink = new();
        using Logger logger = new(new LoggerOptions { Sinks = [sink], MinimumLevel = LogLevel.Information });
        LogCategory category = logger.CreateCategory("Allocation");
        LogCategory silenced = logger.CreateCategory("Silenced");
        logger.SetLevel(silenced, LogLevel.None);
        string longText = new('a', 5000);

        for (int i = 1; i <= WarmUpIterations; i++)
        {
            LogDisabled(category, silenced, i, longText);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 1; i <= Iterations; i++)
        {
            LogDisabled(category, silenced, i, longText);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.Equal(0, sink.WriteCount);
    }

    private static void LogCommonTypes(LogCategory category, int i, string longText)
    {
        ReadOnlySpan<char> chars = "chars";
        category.Information(
            $"int {i} long {i * 3L} float {i * 0.5f} double {i * 0.25:F2} bool {i % 2 == 0} string {"text"} chars {chars} utf8 {"bytes"u8}");
        category.Log(LogLevel.Warning, $"dynamic {i} {-i * 1.5}");
        category.Trace($"{i:X8} {(uint)i} {(byte)i} {(short)i} {(ulong)i} {(char)('a' + (i % 26))}");
        category.Debug($"truncated {longText}");
    }

    private static void LogDisabled(LogCategory category, LogCategory silenced, int i, string longText)
    {
        ReadOnlySpan<char> chars = "chars";
        category.Trace($"int {i} long {i * 3L} float {i * 0.5f} double {i * 0.25:F2} bool {i % 2 == 0} string {longText} chars {chars} utf8 {"bytes"u8}");
        category.Debug($"{i}");
        category.Log(LogLevel.Debug, $"{i}");
        silenced.Critical($"{i} {longText}");
        default(LogCategory).Error($"{i}");
    }

    /// <summary>Counts writes and touches the message, without allocating.</summary>
    private sealed class CountingSink : ILogSink
    {
        public int WriteCount { get; private set; }

        public long ByteCount { get; private set; }

        public void Write(scoped in LogEntry entry)
        {
            WriteCount++;
            ByteCount += entry.Message.Length;
        }

        public void Flush()
        {
        }
    }
}
