using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using BrutalEngine.Core.Logging;
using BrutalEngine.Core.Logging.Sinks;
using Xunit;

namespace BrutalEngine.Core.Tests.Logging.Sinks;

public sealed class ConsoleLogSinkTests
{
    private const string Prefix = "1970-01-01T00:00:00.000Z";

    [Theory]
    [InlineData(LogLevel.Trace, "TRCE")]
    [InlineData(LogLevel.Debug, "DBUG")]
    [InlineData(LogLevel.Information, "INFO")]
    public void Write_BelowThreshold_GoesToOutput(LogLevel level, string token)
    {
        RecordingStream output = new();
        RecordingStream errorOutput = new();
        using ConsoleLogSink sink = new(new ConsoleLogSinkOptions { Output = output, ErrorOutput = errorOutput });

        Write(sink, level, "hello");

        Assert.Equal([$"{Prefix} {token} [Core] hello\n"], output.Writes);
        Assert.Empty(errorOutput.Writes);
    }

    [Theory]
    [InlineData(LogLevel.Warning, LogLevel.Warning, "WARN")]
    [InlineData(LogLevel.Warning, LogLevel.Error, "EROR")]
    [InlineData(LogLevel.Warning, LogLevel.Critical, "CRIT")]
    [InlineData(LogLevel.Trace, LogLevel.Trace, "TRCE")]
    [InlineData(LogLevel.Critical, LogLevel.Critical, "CRIT")]
    public void Write_AtOrAboveThreshold_GoesToErrorOutput(LogLevel threshold, LogLevel level, string token)
    {
        RecordingStream output = new();
        RecordingStream errorOutput = new();
        using ConsoleLogSink sink = new(new ConsoleLogSinkOptions { Output = output, ErrorOutput = errorOutput, ErrorThreshold = threshold });

        Write(sink, level, "hello");

        Assert.Empty(output.Writes);
        Assert.Equal([$"{Prefix} {token} [Core] hello\n"], errorOutput.Writes);
    }

    [Fact]
    public void Write_ThresholdNone_SendsEverythingToOutput()
    {
        RecordingStream output = new();
        RecordingStream errorOutput = new();
        using ConsoleLogSink sink = new(new ConsoleLogSinkOptions { Output = output, ErrorOutput = errorOutput, ErrorThreshold = LogLevel.None });

        Write(sink, LogLevel.Critical, "a");
        Write(sink, LogLevel.Error, "b");

        Assert.Equal(2, output.Writes.Count);
        Assert.Empty(errorOutput.Writes);
    }

    [Fact]
    public void Write_Error_FlushesImmediately()
    {
        RecordingStream output = new();
        RecordingStream errorOutput = new();
        using ConsoleLogSink sink = new(new ConsoleLogSinkOptions { Output = output, ErrorOutput = errorOutput, ErrorThreshold = LogLevel.Information });

        Write(sink, LogLevel.Debug, "below threshold");
        Write(sink, LogLevel.Information, "info");
        Write(sink, LogLevel.Warning, "warning");
        Assert.Equal(0, output.FlushCount);
        Assert.Equal(0, errorOutput.FlushCount);

        Write(sink, LogLevel.Error, "error");
        Assert.Equal(1, errorOutput.FlushCount);
        Assert.Equal(4, output.Writes.Count + errorOutput.Writes.Count);

        Write(sink, LogLevel.Critical, "critical");
        Assert.Equal(2, errorOutput.FlushCount);
        Assert.Equal(0, output.FlushCount);
    }

    [Fact]
    public void Write_ErrorBelowThreshold_FlushesOutput()
    {
        RecordingStream output = new();
        using ConsoleLogSink sink = new(new ConsoleLogSinkOptions { Output = output, ErrorOutput = new RecordingStream(), ErrorThreshold = LogLevel.None });

        Write(sink, LogLevel.Error, "error");

        Assert.Equal(1, output.FlushCount);
    }

    [Fact]
    public void Write_WithCallerInfo_AppendsFileAndLine()
    {
        RecordingStream output = new();
        using ConsoleLogSink sink = new(new ConsoleLogSinkOptions { Output = output, ErrorOutput = output, IncludeCallerInfo = true });
        LogEntry entry = new()
        {
            Level = LogLevel.Information,
            Category = "Core",
            Timestamp = DateTimeOffset.UnixEpoch,
            Message = "m"u8,
            CallerFilePath = "/src/A.cs",
            CallerLineNumber = 9,
        };

        sink.Write(entry);

        Assert.Equal([$"{Prefix} INFO [Core] m (/src/A.cs:9)\n"], output.Writes);
    }

    [Fact]
    public void Write_LineLongerThanBuffer_WritesWholeLineInOneCall()
    {
        RecordingStream output = new();
        using ConsoleLogSink sink = new(new ConsoleLogSinkOptions { Output = output, ErrorOutput = output });
        string first = new('x', 70_000);
        string second = new('\u00e9', 50_000);

        Write(sink, LogLevel.Information, "short");
        Write(sink, LogLevel.Information, first);
        Write(sink, LogLevel.Information, second);

        Assert.Equal(
            [$"{Prefix} INFO [Core] short\n", $"{Prefix} INFO [Core] {first}\n", $"{Prefix} INFO [Core] {second}\n"],
            output.Writes);
    }

    [Fact]
    public void Write_FromLogger_WritesTheLoggedLine()
    {
        RecordingStream output = new();
        using ConsoleLogSink sink = new(new ConsoleLogSinkOptions { Output = output, ErrorOutput = output });
        using (Logger logger = new(new LoggerOptions { Sinks = [sink], TimeProvider = new FixedTimeProvider(DateTimeOffset.UnixEpoch) }))
        {
            logger.CreateCategory("Game").Warning($"{3} lives left");
        }

        Assert.Equal([$"{Prefix} WARN [Game] 3 lives left\n"], output.Writes);
    }

    [Fact]
    public void Write_FromManyThreads_WritesWholeLines()
    {
        const int ThreadCount = 8;
        const int EntriesPerThread = 500;
        RecordingStream output = new();
        RecordingStream errorOutput = new();
        using ConsoleLogSink sink = new(new ConsoleLogSinkOptions { Output = output, ErrorOutput = errorOutput });
        using Barrier start = new(ThreadCount);

        var threads = new Thread[ThreadCount];
        for (int t = 0; t < ThreadCount; t++)
        {
            int threadIndex = t;
            threads[t] = new Thread(() =>
            {
                // Messages of different lengths, so a torn or interleaved write would be visible.
                string padding = new((char)('a' + threadIndex), 10 + (threadIndex * 37));
                start.SignalAndWait(TestContext.Current.CancellationToken);
                for (int i = 0; i < EntriesPerThread; i++)
                {
                    Write(sink, i % 3 == 0 ? LogLevel.Warning : LogLevel.Information, $"{threadIndex}:{i}:{padding}");
                }
            });
            threads[t].Start();
        }

        foreach (Thread thread in threads)
        {
            thread.Join();
        }

        Assert.Equal(0, output.OverlappingWrites + errorOutput.OverlappingWrites);
        string[] expected = [.. Enumerable.Range(0, ThreadCount).SelectMany(t => Enumerable.Range(0, EntriesPerThread).Select(i =>
            $"{Prefix} {(i % 3 == 0 ? "WARN" : "INFO")} [Core] {t}:{i}:{new string((char)('a' + t), 10 + (t * 37))}\n"))];
        Assert.Equal(expected.Order(StringComparer.Ordinal), output.Writes.Concat(errorOutput.Writes).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(LogLevel.None)]
    [InlineData((LogLevel)7)]
    public void Write_LevelOutOfRange_ThrowsAndWritesNothing(LogLevel level)
    {
        RecordingStream output = new();
        using ConsoleLogSink sink = new(new ConsoleLogSinkOptions { Output = output, ErrorOutput = output });

        _ = Assert.Throws<ArgumentOutOfRangeException>(() => Write(sink, level, "m"));
        Assert.Empty(output.Writes);
    }

    [Fact]
    public void Flush_FlushesBothOutputs()
    {
        RecordingStream output = new();
        RecordingStream errorOutput = new();
        using ConsoleLogSink sink = new(new ConsoleLogSinkOptions { Output = output, ErrorOutput = errorOutput });

        sink.Flush();

        Assert.Equal(1, output.FlushCount);
        Assert.Equal(1, errorOutput.FlushCount);
    }

    [Fact]
    public void Dispose_GivenStreams_FlushesButDoesNotDisposeThem()
    {
        RecordingStream output = new();
        RecordingStream errorOutput = new();
        ConsoleLogSink sink = new(new ConsoleLogSinkOptions { Output = output, ErrorOutput = errorOutput });

        sink.Dispose();
        sink.Dispose();

        Assert.False(output.IsDisposed);
        Assert.False(errorOutput.IsDisposed);
        Assert.Equal(1, output.FlushCount);
        Assert.Equal(1, errorOutput.FlushCount);
    }

    [Fact]
    public void Dispose_BothStreamsOpened_DisposesBoth()
    {
        RecordingStream openedOutput = new();
        RecordingStream openedErrorOutput = new();
        ConsoleLogSink sink = new(new ConsoleLogSinkOptions(), () => openedOutput, () => openedErrorOutput);

        Write(sink, LogLevel.Information, "out");
        Write(sink, LogLevel.Error, "err");
        sink.Dispose();

        Assert.Equal([$"{Prefix} INFO [Core] out\n"], openedOutput.Writes);
        Assert.Equal([$"{Prefix} EROR [Core] err\n"], openedErrorOutput.Writes);
        Assert.True(openedOutput.IsDisposed);
        Assert.True(openedErrorOutput.IsDisposed);
    }

    [Fact]
    public void Dispose_OutputOpenedErrorOutputGiven_DisposesOnlyOpenedOutput()
    {
        RecordingStream openedOutput = new();
        RecordingStream given = new();
        ConsoleLogSink sink = new(new ConsoleLogSinkOptions { ErrorOutput = given }, () => openedOutput, () => throw new InvalidOperationException("Not opened."));

        sink.Dispose();

        Assert.True(openedOutput.IsDisposed);
        Assert.False(given.IsDisposed);
    }

    [Fact]
    public void Dispose_OutputGivenErrorOutputOpened_DisposesOnlyOpenedErrorOutput()
    {
        RecordingStream given = new();
        RecordingStream openedErrorOutput = new();
        ConsoleLogSink sink = new(new ConsoleLogSinkOptions { Output = given }, () => throw new InvalidOperationException("Not opened."), () => openedErrorOutput);

        sink.Dispose();

        Assert.True(openedErrorOutput.IsDisposed);
        Assert.False(given.IsDisposed);
    }

    [Fact]
    public void Constructor_OpeningErrorOutputThrows_DisposesOpenedOutputAndPropagates()
    {
        RecordingStream openedOutput = new();
        InvalidOperationException failure = new("Can't open standard error (test).");

        InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(
            () => new ConsoleLogSink(new ConsoleLogSinkOptions(), () => openedOutput, () => throw failure));

        Assert.Same(failure, thrown);
        Assert.True(openedOutput.IsDisposed);
    }

    [Fact]
    public void Write_AfterDispose_ThrowsObjectDisposed()
    {
        RecordingStream output = new();
        ConsoleLogSink sink = new(new ConsoleLogSinkOptions { Output = output, ErrorOutput = output });
        sink.Dispose();

        _ = Assert.Throws<ObjectDisposedException>(() => Write(sink, LogLevel.Information, "m"));
        Assert.Empty(output.Writes);
    }

    [Fact]
    public void Flush_AfterDispose_ThrowsObjectDisposed()
    {
        RecordingStream output = new();
        ConsoleLogSink sink = new(new ConsoleLogSinkOptions { Output = output, ErrorOutput = output });
        sink.Dispose();

        _ = Assert.Throws<ObjectDisposedException>(sink.Flush);
        Assert.Equal(1, output.FlushCount);
    }

    [Fact]
    public void Write_StreamThrows_PropagatesAndSinkStaysUsable()
    {
        RecordingStream output = new() { FailingWrites = 1 };
        using ConsoleLogSink sink = new(new ConsoleLogSinkOptions { Output = output, ErrorOutput = output });

        _ = Assert.Throws<IOException>(() => Write(sink, LogLevel.Information, "lost"));
        Write(sink, LogLevel.Information, "next");

        Assert.Equal([$"{Prefix} INFO [Core] next\n"], output.Writes);
    }

    [Fact]
    public void Write_StreamThrowsUnderLogger_CountsOneSinkFailureAndKeepsLogging()
    {
        RecordingStream output = new() { FailingWrites = 1 };
        using ConsoleLogSink sink = new(new ConsoleLogSinkOptions { Output = output, ErrorOutput = output });
        using Logger logger = new(new LoggerOptions { Sinks = [sink], TimeProvider = new FixedTimeProvider(DateTimeOffset.UnixEpoch) });
        LogCategory category = logger.CreateCategory("Game");

        category.Information($"lost");
        category.Information($"kept");

        Assert.Equal(1, logger.SinkFailureCount);
        Assert.Equal([$"{Prefix} INFO [Game] kept\n"], output.Writes);
    }

    [Fact]
    public void Constructor_InvalidOptions_Throws()
    {
        using RecordingStream readOnly = new(writable: false);

        _ = Assert.Throws<ArgumentNullException>(() => new ConsoleLogSink(null!));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new ConsoleLogSink(new ConsoleLogSinkOptions { Output = Stream.Null, ErrorOutput = Stream.Null, ErrorThreshold = (LogLevel)7 }));
        _ = Assert.Throws<ArgumentException>(() => new ConsoleLogSink(new ConsoleLogSinkOptions { Output = readOnly, ErrorOutput = Stream.Null }));
        _ = Assert.Throws<ArgumentException>(() => new ConsoleLogSink(new ConsoleLogSinkOptions { Output = Stream.Null, ErrorOutput = readOnly }));
    }

    [Fact]
    public void Properties_ReflectOptions()
    {
        using ConsoleLogSink sink = new(new ConsoleLogSinkOptions { Output = Stream.Null, ErrorOutput = Stream.Null, ErrorThreshold = LogLevel.Error, IncludeCallerInfo = true });

        Assert.Equal(LogLevel.Error, sink.ErrorThreshold);
        Assert.True(sink.IncludeCallerInfo);
    }

    private static void Write(ConsoleLogSink sink, LogLevel level, string message)
    {
        LogEntry entry = new() { Level = level, Category = "Core", Timestamp = DateTimeOffset.UnixEpoch, Message = Encoding.UTF8.GetBytes(message) };
        sink.Write(entry);
    }

    /// <summary>Records each write call as one string, counts flushes, and notices overlapping writes.</summary>
    private sealed class RecordingStream(bool writable = true) : Stream
    {
        private int _writing;

        public List<string> Writes { get; } = [];

        public int FlushCount { get; private set; }

        public int OverlappingWrites { get; private set; }

        public bool IsDisposed { get; private set; }

        /// <summary>How many of the next write calls throw an <see cref="IOException"/> instead of recording.</summary>
        public int FailingWrites { get; set; }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => writable && !IsDisposed;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count) => Record(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer) => Record(buffer);

        public override void Flush() => FlushCount++;

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }

        private void Record(ReadOnlySpan<byte> bytes)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            if (FailingWrites > 0)
            {
                FailingWrites--;
                throw new IOException("Stream write failure (test).");
            }

            if (Interlocked.Exchange(ref _writing, 1) != 0)
            {
                OverlappingWrites++;
            }

            Writes.Add(Encoding.UTF8.GetString(bytes));
            _ = Interlocked.Exchange(ref _writing, 0);
        }
    }
}
