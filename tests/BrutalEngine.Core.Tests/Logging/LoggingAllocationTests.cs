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

    [Fact]
    public void Log_EnabledCustomUtf8Formattable_DoesNotAllocate()
    {
        CountingSink sink = new();
        using Logger logger = new(new LoggerOptions { Sinks = [sink] });
        LogCategory category = logger.CreateCategory("Allocation");

        for (int i = 1; i <= WarmUpIterations; i++)
        {
            LogCustom(category, i);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 1; i <= Iterations; i++)
        {
            LogCustom(category, i);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.Equal((WarmUpIterations + Iterations) * 2, sink.WriteCount);
    }

    [Fact]
    public void LoggerQueries_DoNotAllocate()
    {
        using Logger logger = new(new LoggerOptions { Sinks = [new CountingSink()] });
        LogCategory category = logger.CreateCategory("Allocation");
        long failures = 0;
        int enabled = 0;

        for (int i = 0; i < WarmUpIterations; i++)
        {
            Query(logger, category, i, ref enabled, ref failures);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < Iterations; i++)
        {
            Query(logger, category, i, ref enabled, ref failures);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.Equal(0, failures);
        Assert.True(enabled > 0);

        static void Query(Logger logger, LogCategory category, int i, ref int enabled, ref long failures)
        {
            logger.SetLevel(category, (LogLevel)(i % 7));
            enabled += logger.IsEnabled(category, LogLevel.Warning) ? 1 : 0;
            enabled += category.IsEnabled(LogLevel.Error) ? 1 : 0;
            failures += logger.SinkFailureCount;
        }
    }

    private static void LogCustom(LogCategory category, int i)
    {
        Vector2Value position = new(i, i * 0.5f);
        category.Information($"position {position}");
        category.Warning($"velocity {new Vector2Value(-i, 2.5f):F2}");
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

    /// <summary>A custom value type that formats itself to UTF-8, like the engine's maths types.</summary>
    private readonly struct Vector2Value(float x, float y) : IUtf8SpanFormattable
    {
        public bool TryFormat(Span<byte> utf8Destination, out int bytesWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
        {
            bytesWritten = 0;
            int position = 0;
            if (!TryWrite(utf8Destination, ref position, "("u8)
                || !x.TryFormat(utf8Destination[position..], out int written, format, provider)
                || !TryWrite(utf8Destination, ref position, written, ", "u8)
                || !y.TryFormat(utf8Destination[position..], out written, format, provider)
                || !TryWrite(utf8Destination, ref position, written, ")"u8))
            {
                return false;
            }

            bytesWritten = position;
            return true;
        }

        private static bool TryWrite(Span<byte> destination, ref int position, ReadOnlySpan<byte> text) =>
            TryWrite(destination, ref position, 0, text);

        private static bool TryWrite(Span<byte> destination, ref int position, int advance, ReadOnlySpan<byte> text)
        {
            position += advance;
            if (!text.TryCopyTo(destination[position..]))
            {
                return false;
            }

            position += text.Length;
            return true;
        }
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
