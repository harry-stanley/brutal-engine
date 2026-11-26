using System;
using System.Buffers;
using System.Reflection;
using BrutalEngine.Core.Logging;
using Xunit;

namespace BrutalEngine.Core.Tests.Logging;

public sealed class LoggingAllocationTests
{
    private const int Iterations = 1000;
    private const int WarmUpIterations = 3;

    // Bounds the loop that empties the shared pool of one array size, so a pool that never runs dry fails the test
    // instead of hanging it. The pool keeps at most 32 arrays per size per processor, by default.
    private const int MaxDrainedArrays = 100_000;

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

    [Fact]
    public void SetLevel_AfterGarbageCollection_DoesNotAllocate()
    {
        using Logger logger = new(new LoggerOptions { Sinks = [new CountingSink()] });
        LogCategory category = logger.CreateCategory("Allocation");
        for (int i = 0; i < WarmUpIterations; i++)
        {
            logger.SetLevel(category, (LogLevel)(i % 7));
        }

        // A collection, from this thread or any other, can drop runtime caches that are only weakly held. A query
        // that needs such a cache rebuilds it on the next call, so a collection inside a measured window shows up
        // as allocation. Collect deterministically here instead of waiting for a parallel test to do it.
        GC.Collect();
        long before = GC.GetAllocatedBytesForCurrentThread();
        logger.SetLevel(category, LogLevel.Warning);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.True(category.IsEnabled(LogLevel.Warning));
        Assert.False(category.IsEnabled(LogLevel.Information));
    }

    [Fact]
    public void Log_AfterSharedPoolTrim_DoesNotAllocate()
    {
        CountingSink sink = new();
        using Logger logger = new(new LoggerOptions { Sinks = [sink], MinimumLevel = LogLevel.Trace });
        LogCategory category = logger.CreateCategory("Allocation");
        string longText = new('a', 5000);
        for (int i = 1; i <= WarmUpIterations; i++)
        {
            LogCommonTypes(category, i, longText);
            LogCustom(category, i);
        }

        // A gen2 collection under high memory pressure trims ArrayPool<byte>.Shared between warm-up and the window.
        // Logging must not depend on the pool still holding a message buffer.
        byte[][] drained = TrimSharedPool(LoggerOptions.DefaultMaxMessageBytes);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 1; i <= Iterations; i++)
        {
            LogCommonTypes(category, i, longText);
            LogCustom(category, i);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        ReturnToSharedPool(drained);

        Assert.Equal(0, allocated);
        Assert.Equal((WarmUpIterations + Iterations) * 6, sink.WriteCount);
    }

    [Fact]
    public void Log_AfterFormattingThrows_DoesNotAllocate()
    {
        CountingSink sink = new();
        using Logger logger = new(new LoggerOptions { Sinks = [sink] });
        LogCategory category = logger.CreateCategory("Allocation");
        for (int i = 1; i <= WarmUpIterations; i++)
        {
            LogCustom(category, i);
        }

        // More failed messages than the logger keeps buffers, through each formatting path: each one must give its
        // buffer back, or the logger runs out of its own buffers and falls back to the shared pool, which a trim
        // then empties.
        ThrowingObject throwingObject = new();
        for (int i = 0; i < 2 * MessageBufferCache.DefaultSlotCount; i++)
        {
            _ = Assert.Throws<FormatException>(() => category.Information($"value {new ThrowingValue()}"));
            _ = Assert.Throws<FormatException>(() => category.Information($"object {throwingObject}"));
        }

        byte[][] drained = TrimSharedPool(LoggerOptions.DefaultMaxMessageBytes);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 1; i <= Iterations; i++)
        {
            LogCustom(category, i);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        ReturnToSharedPool(drained);

        Assert.Equal(0, allocated);
        Assert.Equal((WarmUpIterations + Iterations) * 2, sink.WriteCount);
    }

    // Does to this thread what a trim of ArrayPool<byte>.Shared under high memory pressure does to every thread:
    // SharedArrayPool<T>.Trim, which runs after each gen2 collection, clears every thread's cached arrays when the
    // memory load is at least 90% of the GC's high-load threshold. Then it rents every array of this length the pool
    // still holds, until a rent has to allocate, so no other thread's returned array can stand in for the dropped one.
    // The caller keeps the arrays until its window closes. Reflection on the runtime's private field is for this test
    // only; the field's absence fails the test rather than letting it pass without a trim.
    private static byte[][] TrimSharedPool(int length)
    {
        FieldInfo? threadArrays = ArrayPool<byte>.Shared.GetType().GetField("t_tlsBuckets", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.True(threadArrays is not null, "ArrayPool<byte>.Shared has no thread-local array field to clear.");
        if (threadArrays.GetValue(null) is Array cached)
        {
            Array.Clear(cached);
        }

        byte[][] drained = new byte[MaxDrainedArrays][];
        for (int i = 0; i < drained.Length; i++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            drained[i] = ArrayPool<byte>.Shared.Rent(length);
            if (GC.GetAllocatedBytesForCurrentThread() != before)
            {
                return drained[..(i + 1)];
            }
        }

        Assert.Fail($"ArrayPool<byte>.Shared still had arrays of {length} bytes after {MaxDrainedArrays} rents.");
        return [];
    }

    private static void ReturnToSharedPool(byte[][] arrays)
    {
        foreach (byte[] array in arrays)
        {
            ArrayPool<byte>.Shared.Return(array);
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

    /// <summary>A value whose formatting always fails, like a buggy custom formatter.</summary>
    private readonly struct ThrowingValue : IUtf8SpanFormattable
    {
        public bool TryFormat(Span<byte> utf8Destination, out int bytesWritten, ReadOnlySpan<char> format, IFormatProvider? provider) =>
            throw new FormatException("This value can't be formatted.");
    }

    /// <summary>A reference type without a UTF-8 formatter, so it takes the fallback path, whose ToString fails.</summary>
    private sealed class ThrowingObject
    {
        public override string ToString() => throw new FormatException("This object can't be formatted.");
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
