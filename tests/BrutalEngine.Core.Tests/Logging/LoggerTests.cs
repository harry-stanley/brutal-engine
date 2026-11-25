using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
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
    }

    [Fact]
    public void CreateCategory_NullName_Throws()
    {
        using Logger logger = CreateLogger(new RecordingSink());

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
    }

    [Fact]
    public void IsEnabled_ForeignOrDefaultCategory_Throws()
    {
        using Logger logger = CreateLogger(new RecordingSink());
        using Logger other = CreateLogger(new RecordingSink());
        LogCategory category = logger.CreateCategory("Core");

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

    [Fact]
    public void Log_BelowMinimumLevel_DoesNotReachSinks()
    {
        RecordingSink sink = new();
        using Logger logger = CreateLogger(sink, LogLevel.Warning);
        LogCategory category = logger.CreateCategory("Core");

        category.Trace($"trace");
        category.Debug($"debug");
        category.Information($"information");
        category.Log(LogLevel.Information, $"dynamic information");
        category.Log(LogLevel.None, $"none");
        category.Warning($"warning");

        Assert.Equal(["warning"], sink.Messages);
    }

    [Fact]
    public void Log_Enabled_ReachesEverySinkOnceInOrder()
    {
        RecordingSink first = new();
        RecordingSink second = new();
        using Logger logger = new(new LoggerOptions { Sinks = [first, second], MinimumLevel = LogLevel.Trace });
        LogCategory category = logger.CreateCategory("Core");

        category.Trace($"1");
        category.Debug($"2");
        category.Information($"3");
        category.Warning($"4");
        category.Error($"5");
        category.Critical($"6");
        category.Log(LogLevel.Error, $"7");

        LogLevel[] expectedLevels =
        [
            LogLevel.Trace, LogLevel.Debug, LogLevel.Information, LogLevel.Warning, LogLevel.Error, LogLevel.Critical, LogLevel.Error,
        ];
        foreach (RecordingSink sink in new[] { first, second })
        {
            Assert.Equal(["1", "2", "3", "4", "5", "6", "7"], sink.Messages);
            Assert.Equal(expectedLevels, sink.Entries.ConvertAll(e => e.Level));
            Assert.All(sink.Entries, e => Assert.Equal("Core", e.Category));
        }
    }

    [Fact]
    public void Log_StampsTimeThreadAndCallerInfo()
    {
        DateTimeOffset now = new(2026, 11, 25, 9, 30, 15, 250, TimeSpan.Zero);
        RecordingSink sink = new();
        using Logger logger = new(new LoggerOptions { Sinks = [sink], TimeProvider = new FixedTimeProvider(now) });
        LogCategory category = logger.CreateCategory("Physics");

        int expectedLine = CurrentLine() + 1;
        category.Error($"Step {3} failed");

        RecordedEntry entry = Assert.Single(sink.Entries);
        Assert.Equal(now, entry.Timestamp);
        Assert.Equal(Environment.CurrentManagedThreadId, entry.ThreadId);
        Assert.EndsWith(nameof(LoggerTests) + ".cs", entry.CallerFilePath, StringComparison.Ordinal);
        Assert.Equal(expectedLine, entry.CallerLineNumber);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Equal("Physics", entry.Category);
        Assert.Equal("Step 3 failed", entry.Message);
    }

    [Fact]
    public void Log_SinkThrows_OtherSinksStillReceiveAndFailureIsCounted()
    {
        RecordingSink failing = new() { ThrowOnWrite = true };
        RecordingSink healthy = new();
        using Logger logger = CreateLogger(failing, healthy);
        LogCategory category = logger.CreateCategory("Core");

        category.Information($"first");
        category.Error($"second");

        Assert.Equal(["first", "second"], healthy.Messages);
        Assert.Equal(2, logger.SinkFailureCount);
    }

    [Fact]
    public void Log_FromInsideASink_IsDroppedInsteadOfRecursing()
    {
        ReentrantSink reentrant = new();
        RecordingSink recorder = new();
        using Logger logger = new(new LoggerOptions { Sinks = [reentrant, recorder] });
        LogCategory category = logger.CreateCategory("Core");
        reentrant.Category = category;

        category.Information($"outer");

        Assert.Equal(["outer"], recorder.Messages);
        Assert.Equal(1, reentrant.WriteCount);
        Assert.Equal(0, logger.SinkFailureCount);
    }

    [Fact]
    public void Log_HandlerBuiltForAnotherCategoryOrLevel_Throws()
    {
        RecordingSink sink = new();
        using Logger logger = CreateLogger(sink);
        LogCategory core = logger.CreateCategory("Core");
        LogCategory audio = logger.CreateCategory("Audio");

        _ = Assert.Throws<ArgumentException>(() =>
        {
            LogMessageHandler<InformationLevel> handler = new(0, 0, core, out _);
            audio.Information(ref handler);
        });
        _ = Assert.Throws<ArgumentException>(() =>
        {
            LogMessageHandler<DynamicLevel> handler = new(0, 0, core, LogLevel.Error, out _);
            core.Log(LogLevel.Warning, ref handler);
        });

        Assert.Empty(sink.Entries);
    }

    [Fact]
    public void Log_FromManyThreads_EveryEntryDeliveredOnceAndWhole()
    {
        const int ThreadCount = 8;
        const int EntriesPerThread = 2000;
        SerialCheckingSink sink = new();
        using Logger logger = new(new LoggerOptions { Sinks = [sink] });
        LogCategory category = logger.CreateCategory("Threads");
        using Barrier start = new(ThreadCount);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        var threads = new Thread[ThreadCount];
        var failures = new Exception?[ThreadCount];
        for (int t = 0; t < ThreadCount; t++)
        {
            int thread = t;
            threads[t] = new Thread(() =>
            {
                try
                {
                    start.SignalAndWait(cancellationToken);
                    for (int i = 0; i < EntriesPerThread; i++)
                    {
                        category.Information($"thread {thread} entry {i} value {i * 0.5} end");
                    }
                }
                catch (Exception exception)
                {
                    // Reported below: an exception escaping a raw thread would crash the test host.
                    failures[thread] = exception;
                }
            });
            threads[t].Start();
        }

        foreach (Thread thread in threads)
        {
            thread.Join();
        }

        Assert.All(failures, Assert.Null);
        Assert.Equal(0, sink.OverlappingWrites);
        Assert.Equal(ThreadCount * EntriesPerThread, sink.Messages.Count);
        int[] nextEntry = new int[ThreadCount];
        foreach (string message in sink.Messages)
        {
            string[] parts = message.Split(' ');
            Assert.Equal(7, parts.Length);
            int thread = int.Parse(parts[1], CultureInfo.InvariantCulture);
            int entry = int.Parse(parts[3], CultureInfo.InvariantCulture);
            Assert.Equal(nextEntry[thread], entry);
            Assert.Equal(FormattableString.Invariant($"thread {thread} entry {entry} value {entry * 0.5} end"), message);
            nextEntry[thread]++;
        }

        Assert.All(nextEntry, count => Assert.Equal(EntriesPerThread, count));
    }

    [Fact]
    public void Log_TimeProviderThrows_PropagatesAndLaterEntriesStillLog()
    {
        RecordingSink sink = new();
        ThrowingOnceTimeProvider clock = new();
        using Logger logger = new(new LoggerOptions { Sinks = [sink], TimeProvider = clock });
        LogCategory category = logger.CreateCategory("Core");

        _ = Assert.Throws<InvalidOperationException>(() => category.Information($"lost"));
        category.Information($"kept");

        Assert.Equal(["kept"], sink.Messages);
        Assert.Equal(0, logger.SinkFailureCount);
    }

    [Fact]
    public void LogAfterDispose_IsNoOp()
    {
        RecordingSink sink = new();
        Logger logger = CreateLogger(sink);
        LogCategory category = logger.CreateCategory("Core");
        logger.Dispose();

        category.Critical($"late {42}");
        category.Log(LogLevel.Critical, $"late");

        Assert.Empty(sink.Entries);
    }

    private static Logger CreateLogger(RecordingSink sink, LogLevel minimumLevel = LogLevel.Information) =>
        new(new LoggerOptions { Sinks = [sink], MinimumLevel = minimumLevel });

    private static Logger CreateLogger(params RecordingSink[] sinks) =>
        new(new LoggerOptions { Sinks = sinks });

    private static int CurrentLine([CallerLineNumber] int line = 0) => line;

    /// <summary>A clock that throws on its first call, then returns the epoch.</summary>
    private sealed class ThrowingOnceTimeProvider : TimeProvider
    {
        private bool _thrown;

        public override DateTimeOffset GetUtcNow()
        {
            if (!_thrown)
            {
                _thrown = true;
                throw new InvalidOperationException("Clock failure (test).");
            }

            return DateTimeOffset.UnixEpoch;
        }
    }

    /// <summary>Records messages, and counts writes that started while another was still running.</summary>
    private sealed class SerialCheckingSink : ILogSink
    {
        private int _activeWrites;
        private int _overlappingWrites;

        public List<string> Messages { get; } = [];

        public int OverlappingWrites => Volatile.Read(ref _overlappingWrites);

        public void Write(scoped in LogEntry entry)
        {
            if (Interlocked.Increment(ref _activeWrites) != 1)
            {
                _ = Interlocked.Increment(ref _overlappingWrites);
            }

            Messages.Add(Encoding.UTF8.GetString(entry.Message));
            _ = Interlocked.Decrement(ref _activeWrites);
        }

        public void Flush()
        {
        }
    }

    /// <summary>A sink that logs through the logger that is calling it.</summary>
    private sealed class ReentrantSink : ILogSink
    {
        public LogCategory Category { get; set; }

        public int WriteCount { get; private set; }

        public void Write(scoped in LogEntry entry)
        {
            WriteCount++;
            Category.Error($"inner {WriteCount}");
        }

        public void Flush()
        {
        }
    }
}
