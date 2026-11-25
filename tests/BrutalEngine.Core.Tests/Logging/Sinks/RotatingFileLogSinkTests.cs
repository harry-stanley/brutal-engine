using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using BrutalEngine.Core.Logging;
using BrutalEngine.Core.Logging.Sinks;
using CsCheck;
using Xunit;

namespace BrutalEngine.Core.Tests.Logging.Sinks;

/// <summary>
/// xUnit creates one instance per test, so each test gets its own temp directory, deleted afterwards even when the
/// test fails. Tests never hold a handle on a log file across a rotation: Windows can't rename a file that another
/// handle has open without <see cref="FileShare.Delete"/>.
/// </summary>
public sealed class RotatingFileLogSinkTests : IDisposable
{
    private const string Prefix = "1970-01-01T00:00:00.000Z";

    // "<timestamp> INFO [Core] " and the newline around a message.
    private const int LineOverhead = 38;
    private const int SmallestMax = 64 * 1024;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "BrutalEngine.Core.Tests", "RotatingFileLogSink-" + Guid.NewGuid().ToString("N"));

    public RotatingFileLogSinkTests() => Directory.CreateDirectory(_root);

    public void Dispose() => DeleteBestEffort(_root);

    [Fact]
    public void Options_Defaults_MatchTheSpec()
    {
        RotatingFileLogSinkOptions options = new() { Directory = _root };

        Assert.Equal("engine.log", options.FileName);
        Assert.Equal(10L * 1024 * 1024, options.MaxFileBytes);
        Assert.Equal(5, options.MaxFiles);
        Assert.Equal(LogLevel.Warning, options.FlushLevel);
        Assert.False(options.IncludeCallerInfo);
    }

    [Fact]
    public void Construct_ExistingLog_RotatesItToArchiveOne()
    {
        File.WriteAllText(PathOf("engine.log"), "previous run\n");
        File.WriteAllText(PathOf("engine.1.log"), "run before\n");

        new RotatingFileLogSink(Options()).Dispose();

        Assert.Equal(["engine.1.log", "engine.2.log", "engine.log"], Files(_root));
        Assert.Equal("previous run\n", Read("engine.1.log"));
        Assert.Equal("run before\n", Read("engine.2.log"));
        Assert.Equal(string.Empty, Read("engine.log"));
    }

    [Fact]
    public void Construct_MaxFilesOne_DeletesExistingLog()
    {
        File.WriteAllText(PathOf("engine.log"), "previous run\n");

        new RotatingFileLogSink(Options() with { MaxFiles = 1 }).Dispose();

        Assert.Equal(["engine.log"], Files(_root));
        Assert.Equal(string.Empty, Read("engine.log"));
    }

    [Fact]
    public void Construct_StaleArchivesBeyondRetention_AreDeleted()
    {
        foreach (string name in (string[])["engine.2.log", "engine.3.log", "engine.7.log", "engine.100.log", "notes.txt"])
        {
            File.WriteAllText(PathOf(name), name);
        }

        new RotatingFileLogSink(Options() with { MaxFiles = 3 }).Dispose();

        Assert.Equal(["engine.2.log", "engine.log", "notes.txt"], Files(_root));
    }

    [Fact]
    public void Construct_ExistingLogAndArchivesBeyondRetention_RotatesAndTrims()
    {
        // Left by a run with MaxFiles = 5 (or one that crashed mid-rotation), opened with MaxFiles = 3.
        File.WriteAllText(PathOf("engine.log"), "current\n");
        for (int n = 1; n <= 4; n++)
        {
            File.WriteAllText(PathOf($"engine.{n}.log"), $"archive {n}\n");
        }

        new RotatingFileLogSink(Options() with { MaxFiles = 3 }).Dispose();

        Assert.Equal(["engine.1.log", "engine.2.log", "engine.log"], Files(_root));
        Assert.Equal("current\n", Read("engine.1.log"));
        Assert.Equal("archive 1\n", Read("engine.2.log"));
        Assert.Equal(string.Empty, Read("engine.log"));
    }

    [Fact]
    public void Construct_FileNameWithoutExtension_ArchivesAsNameDotNumber()
    {
        File.WriteAllText(PathOf("game"), "previous run\n");

        new RotatingFileLogSink(Options() with { FileName = "game" }).Dispose();

        Assert.Equal(["game", "game.1"], Files(_root));
        Assert.Equal("previous run\n", Read("game.1"));
    }

    [Fact]
    public void Construct_MissingDirectory_CreatesIt()
    {
        string directory = Path.Combine(_root, "a", "b");

        using (RotatingFileLogSink sink = new(Options() with { Directory = directory }))
        {
            Write(sink, LogLevel.Information, "hello");
        }

        Assert.Equal(Line("hello"), Read(Path.Combine(directory, "engine.log")));
    }

    [Fact]
    public void Construct_NullOptions_ThrowsArgumentNull()
    {
        _ = Assert.Throws<ArgumentNullException>(() => new RotatingFileLogSink(null!));
    }

    [Theory]
    [InlineData("directory-null", typeof(ArgumentNullException))]
    [InlineData("directory-empty", typeof(ArgumentException))]
    [InlineData("directory-relative", typeof(ArgumentException))]
    [InlineData("directory-with-nul", typeof(ArgumentException))]
    [InlineData("file-name-null", typeof(ArgumentNullException))]
    [InlineData("file-name-empty", typeof(ArgumentException))]
    [InlineData("file-name-with-slash", typeof(ArgumentException))]
    [InlineData("file-name-with-backslash", typeof(ArgumentException))]
    [InlineData("file-name-dot", typeof(ArgumentException))]
    [InlineData("file-name-dot-dot", typeof(ArgumentException))]
    [InlineData("file-name-with-nul", typeof(ArgumentException))]
    [InlineData("max-file-bytes-below-64-kib", typeof(ArgumentOutOfRangeException))]
    [InlineData("max-file-bytes-negative", typeof(ArgumentOutOfRangeException))]
    [InlineData("max-files-zero", typeof(ArgumentOutOfRangeException))]
    [InlineData("max-files-above-100", typeof(ArgumentOutOfRangeException))]
    [InlineData("flush-level-undefined", typeof(ArgumentOutOfRangeException))]
    public void Construct_InvalidOptions_Throws(string invalid, Type expected)
    {
        string directory = Path.Combine(_root, "not-created");
        RotatingFileLogSinkOptions valid = new() { Directory = directory };
        RotatingFileLogSinkOptions options = invalid switch
        {
            "directory-null" => valid with { Directory = null! },
            "directory-empty" => valid with { Directory = string.Empty },
            "directory-relative" => valid with { Directory = "logs" },
            "directory-with-nul" => valid with { Directory = directory + "\0x" },
            "file-name-null" => valid with { FileName = null! },
            "file-name-empty" => valid with { FileName = string.Empty },
            "file-name-with-slash" => valid with { FileName = "logs/engine.log" },
            "file-name-with-backslash" => valid with { FileName = "logs\\engine.log" },
            "file-name-dot" => valid with { FileName = "." },
            "file-name-dot-dot" => valid with { FileName = ".." },
            "file-name-with-nul" => valid with { FileName = "engine\0.log" },
            "max-file-bytes-below-64-kib" => valid with { MaxFileBytes = SmallestMax - 1 },
            "max-file-bytes-negative" => valid with { MaxFileBytes = -1 },
            "max-files-zero" => valid with { MaxFiles = 0 },
            "max-files-above-100" => valid with { MaxFiles = 101 },
            "flush-level-undefined" => valid with { FlushLevel = (LogLevel)7 },
            _ => throw new ArgumentOutOfRangeException(nameof(invalid), invalid, "Unknown case."),
        };

        ArgumentException thrown = Assert.ThrowsAny<ArgumentException>(() => new RotatingFileLogSink(options));

        Assert.Equal(expected, thrown.GetType());

        // The sink's own check, not a file API's: Directory.CreateDirectory also rejects a NUL, naming "path".
        Assert.Equal("options", thrown.ParamName);
        Assert.False(Directory.Exists(directory), "Misuse must be rejected before the sink touches the disk.");
    }

    [Fact]
    public void Construct_BoundaryOptions_AreAccepted()
    {
        new RotatingFileLogSink(Options() with { MaxFileBytes = SmallestMax, MaxFiles = 1, FlushLevel = LogLevel.None }).Dispose();
        new RotatingFileLogSink(Options() with { Directory = Path.Combine(_root, "b"), MaxFiles = 100, FlushLevel = LogLevel.Trace }).Dispose();

        Assert.Equal(["engine.log"], Files(_root));
        Assert.Equal(["engine.log"], Files(Path.Combine(_root, "b")));
    }

    [Fact]
    public void Write_PastMaxBytes_RotatesAndContinues()
    {
        // Four lines fill the file exactly (no rotation), the fifth rotates, and the sixth fits after it.
        const int LineBytes = SmallestMax / 4;
        string[] lines = [.. Enumerable.Range(0, 6).Select(i => Line(Message(i, LineBytes)))];

        using (RotatingFileLogSink sink = new(Options() with { MaxFileBytes = SmallestMax }))
        {
            for (int i = 0; i < lines.Length; i++)
            {
                Write(sink, LogLevel.Information, Message(i, LineBytes));
            }
        }

        Assert.Equal(["engine.1.log", "engine.log"], Files(_root));
        Assert.Equal(string.Concat(lines[..4]), Read("engine.1.log"));
        Assert.Equal(lines[4] + lines[5], Read("engine.log"));
    }

    [Fact]
    public void Write_PastMaxBytesByOne_Rotates()
    {
        // Three lines of a quarter of the maximum and one a byte longer total MaxFileBytes + 1 (newlines included),
        // so the fourth line must start a new file.
        const int LineBytes = SmallestMax / 4;
        using (RotatingFileLogSink sink = new(Options() with { MaxFileBytes = SmallestMax }))
        {
            for (int i = 0; i < 3; i++)
            {
                Write(sink, LogLevel.Information, Message(i, LineBytes));
            }

            Write(sink, LogLevel.Information, Message(3, LineBytes + 1));
        }

        Assert.Equal(["engine.1.log", "engine.log"], Files(_root));
        Assert.Equal(string.Concat(Enumerable.Range(0, 3).Select(i => Line(Message(i, LineBytes)))), Read("engine.1.log"));
        Assert.Equal(Line(Message(3, LineBytes + 1)), Read("engine.log"));
    }

    [Fact]
    public void Rotate_ManyTimes_KeepsAtMostMaxFiles()
    {
        // Two lines never fit one file, so every write after the first rotates.
        const int LineBytes = 40_000;
        using (RotatingFileLogSink sink = new(Options() with { MaxFileBytes = SmallestMax, MaxFiles = 3 }))
        {
            for (int i = 0; i < 10; i++)
            {
                Write(sink, LogLevel.Information, Message(i, LineBytes));
            }
        }

        Assert.Equal(["engine.1.log", "engine.2.log", "engine.log"], Files(_root));
        Assert.Equal(Line(Message(9, LineBytes)), Read("engine.log"));
        Assert.Equal(Line(Message(8, LineBytes)), Read("engine.1.log"));
        Assert.Equal(Line(Message(7, LineBytes)), Read("engine.2.log"));
    }

    [Fact]
    public void Write_LineLargerThanMax_WrittenWholeToFreshFile()
    {
        string large = new('\u00e9', SmallestMax);
        using (RotatingFileLogSink sink = new(Options() with { MaxFileBytes = SmallestMax }))
        {
            Write(sink, LogLevel.Information, "before");
            Write(sink, LogLevel.Information, large);
            Write(sink, LogLevel.Information, "after");
        }

        Assert.Equal(["engine.1.log", "engine.2.log", "engine.log"], Files(_root));
        Assert.Equal(Line("before"), Read("engine.2.log"));
        Assert.Equal(Line(large), Read("engine.1.log"));
        Assert.Equal(Line("after"), Read("engine.log"));
    }

    [Fact]
    public void Write_LineLargerThanMaxIntoEmptyFile_DoesNotRotate()
    {
        string large = new('x', SmallestMax);
        using (RotatingFileLogSink sink = new(Options() with { MaxFileBytes = SmallestMax }))
        {
            Write(sink, LogLevel.Information, large);
        }

        Assert.Equal(["engine.log"], Files(_root));
        Assert.Equal(Line(large), Read("engine.log"));
    }

    [Fact]
    public void RandomWrites_TotalContentPreservedAcrossFilesWithinRetention()
    {
        // Mostly lines that fit several to a file, sometimes one longer than the maximum.
        var lineBytes = Gen.Frequency((9, Gen.Int[LineOverhead + 7, 20_000]), (1, Gen.Int[SmallestMax - 100, SmallestMax + 5_000]));

        Gen.Select(Gen.Int[1, 4], lineBytes.Array[1, 25]).Sample((maxFiles, lengths) =>
        {
            string directory = Path.Combine(_root, Guid.NewGuid().ToString("N"));
            string[] lines = [.. lengths.Select((length, i) => Line(Message(i, length)))];
            using (RotatingFileLogSink sink = new(Options() with { Directory = directory, MaxFileBytes = SmallestMax, MaxFiles = maxFiles }))
            {
                for (int i = 0; i < lengths.Length; i++)
                {
                    Write(sink, LogLevel.Information, Message(i, lengths[i]));
                }
            }

            // Reference model of the spec, newest file first: a line that would take a non-empty file past the
            // maximum starts a new file, and only the newest maxFiles files are kept. Lines are ASCII.
            List<string> model = [string.Empty];
            foreach (string line in lines)
            {
                if (model[0].Length > 0 && model[0].Length + line.Length > SmallestMax)
                {
                    model.Insert(0, string.Empty);
                }

                model[0] += line;
            }

            string[] kept = [.. model.Take(maxFiles)];
            string[] names = [.. kept.Select((_, n) => n == 0 ? "engine.log" : $"engine.{n}.log")];
            Assert.Equal(names.Order(StringComparer.Ordinal), Files(directory));
            string[] actual = [.. names.Select(name => Read(Path.Combine(directory, name)))];
            Assert.Equal(kept, actual);

            // Read oldest file first, what survives is exactly the newest lines, cut only between two lines.
            string retained = string.Concat(actual.Reverse());
            Assert.True(
                Enumerable.Range(0, lines.Length + 1).Any(first => string.Concat(lines[first..]) == retained),
                "The kept files aren't the newest whole lines in order.");
            DeleteBestEffort(directory);
        });
    }

    [Fact]
    public void Write_AtFlushLevel_IsOnDiskBeforeDispose()
    {
        using RotatingFileLogSink sink = new(Options());

        Write(sink, LogLevel.Information, "buffered");
        Write(sink, LogLevel.Warning, "flushed");

        Assert.Equal(Line("buffered") + Line("flushed", "WARN"), Read("engine.log"));
    }

    [Fact]
    public void Write_BelowFlushLevel_StaysBuffered()
    {
        using RotatingFileLogSink sink = new(Options() with { FlushLevel = LogLevel.Critical });

        Write(sink, LogLevel.Error, "buffered");

        Assert.Equal(string.Empty, Read("engine.log"));
    }

    [Fact]
    public void Write_FlushLevelNone_NeverFlushesOnWrite()
    {
        using RotatingFileLogSink sink = new(Options() with { FlushLevel = LogLevel.None });

        Write(sink, LogLevel.Critical, "buffered");

        Assert.Equal(string.Empty, Read("engine.log"));
    }

    [Fact]
    public void Flush_WritesBufferedLinesToDisk()
    {
        using RotatingFileLogSink sink = new(Options());
        Write(sink, LogLevel.Information, "buffered");

        sink.Flush();

        Assert.Equal(Line("buffered"), Read("engine.log"));
    }

    [Fact]
    public void Dispose_WritesBufferedLinesAndReleasesTheFile()
    {
        RotatingFileLogSink sink = new(Options());
        Write(sink, LogLevel.Information, "buffered");

        sink.Dispose();
        sink.Dispose();

        Assert.Equal(Line("buffered"), Read("engine.log"));

        // Released: an exclusive open (no sharing) succeeds only when the sink no longer holds the file.
        using FileStream exclusive = new(PathOf("engine.log"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public void Write_WithCallerInfo_AppendsFileAndLine()
    {
        using (RotatingFileLogSink sink = new(Options() with { IncludeCallerInfo = true }))
        {
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
        }

        Assert.Equal($"{Prefix} INFO [Core] m (/src/A.cs:9)\n", Read("engine.log"));
    }

    [Theory]
    [InlineData(LogLevel.None)]
    [InlineData((LogLevel)7)]
    public void Write_LevelOutOfRange_ThrowsAndWritesNothing(LogLevel level)
    {
        using (RotatingFileLogSink sink = new(Options()))
        {
            _ = Assert.Throws<ArgumentOutOfRangeException>(() => Write(sink, level, "m"));
        }

        Assert.Equal(string.Empty, Read("engine.log"));
    }

    [Fact]
    public void Write_AfterDispose_ThrowsObjectDisposed()
    {
        RotatingFileLogSink sink = new(Options());
        sink.Dispose();

        _ = Assert.Throws<ObjectDisposedException>(() => Write(sink, LogLevel.Information, "m"));
    }

    [Fact]
    public void Flush_AfterDispose_ThrowsObjectDisposed()
    {
        RotatingFileLogSink sink = new(Options());
        sink.Dispose();

        _ = Assert.Throws<ObjectDisposedException>(sink.Flush);
    }

    [Fact]
    public void Write_FromManyThreads_AllLinesWholeAndPresent()
    {
        const int ThreadCount = 8;
        const int EntriesPerThread = 500;
        using Barrier start = new(ThreadCount);

        // Exceptions (failed asserts and barrier waits included) are collected and checked on the test thread, so
        // a broken sink fails this test instead of crashing the test host.
        ConcurrentQueue<Exception> failures = new();
        var threads = new Thread[ThreadCount];
        using (RotatingFileLogSink sink = new(Options() with { MaxFileBytes = SmallestMax, MaxFiles = 100 }))
        {
            for (int t = 0; t < ThreadCount; t++)
            {
                int thread = t;
                threads[t] = new Thread(() => Capture(failures, () =>
                {
                    start.SignalAndWait(TestContext.Current.CancellationToken);
                    for (int i = 0; i < EntriesPerThread; i++)
                    {
                        Write(sink, LogLevel.Information, ThreadMessage(thread, i));
                    }
                }));
                threads[t].Start();
            }

            foreach (Thread thread in threads)
            {
                thread.Join();
            }
        }

        Assert.Empty(failures);

        // The lines rotate across several files; each file holds whole lines and stays within the maximum.
        List<string> written = [];
        string[] names = Files(_root);
        Assert.True(names.Length > 2, "The test should rotate several times.");
        foreach (string name in names)
        {
            string content = Read(name);
            Assert.True(Encoding.UTF8.GetByteCount(content) <= SmallestMax);
            Assert.EndsWith("\n", content, StringComparison.Ordinal);
            written.AddRange(content.Split('\n')[..^1].Select(line => line + "\n"));
        }

        string[] expected = [.. Enumerable.Range(0, ThreadCount).SelectMany(t => Enumerable.Range(0, EntriesPerThread).Select(i => Line(ThreadMessage(t, i))))];
        Assert.Equal(expected.Order(StringComparer.Ordinal), written.Order(StringComparer.Ordinal));

        // Messages of different lengths per thread, so a torn or interleaved write would be visible.
        static string ThreadMessage(int thread, int i) => $"{thread}:{i}:{new string((char)('a' + thread), 10 + (thread * 37))}";
    }

    [Fact]
    public void Fault_WhenHealthy_IsNullAndNothingIsDropped()
    {
        using RotatingFileLogSink sink = new(Options());

        Write(sink, LogLevel.Information, "kept");

        Assert.Null(sink.Fault);
        Assert.Equal(0, sink.DroppedCount);
    }

    [Fact]
    public void Construct_DirectoryIsAFile_FaultsWithoutThrowing()
    {
        string file = PathOf("not-a-directory");
        File.WriteAllText(file, "untouched");

        using RotatingFileLogSink sink = new(Options() with { Directory = file });

        EngineError fault = Assert.NotNull(sink.Fault);
        Assert.Equal(ErrorKind.Io, fault.Kind);
        Assert.Contains("not-a-directory", fault.Message, StringComparison.Ordinal);
        Assert.Equal(0, sink.DroppedCount);
        Assert.Equal("untouched", File.ReadAllText(file));
    }

    [Fact]
    public void Write_WhenFaulted_CountsDropped()
    {
        string file = PathOf("not-a-directory");
        File.WriteAllText(file, "untouched");
        using RotatingFileLogSink sink = new(Options() with { Directory = file });
        EngineError? fault = sink.Fault;

        Write(sink, LogLevel.Information, "a");
        Write(sink, LogLevel.Critical, "b");
        sink.Flush();
        Write(sink, LogLevel.Debug, "c");

        Assert.Equal(3, sink.DroppedCount);
        Assert.Equal(fault, sink.Fault);
    }

    [Theory]
    [InlineData(LogLevel.None)]
    [InlineData((LogLevel)7)]
    public void Write_WhenFaultedWithLevelOutOfRange_StillThrows(LogLevel level)
    {
        string file = PathOf("not-a-directory");
        File.WriteAllText(file, "untouched");
        using RotatingFileLogSink sink = new(Options() with { Directory = file });

        _ = Assert.Throws<ArgumentOutOfRangeException>(() => Write(sink, level, "m"));
        Assert.Equal(0, sink.DroppedCount);
    }

    [Fact]
    public void Write_RotationFails_FaultsAndDropsTheLine()
    {
        using (RotatingFileLogSink sink = new(Options() with { MaxFileBytes = SmallestMax, MaxFiles = 2 }))
        {
            BlockArchiveOne();
            Write(sink, LogLevel.Information, Message(0, 40_000));

            Write(sink, LogLevel.Information, Message(1, 40_000));

            EngineError fault = Assert.NotNull(sink.Fault);
            Assert.Equal(ErrorKind.Io, fault.Kind);
            Assert.StartsWith("RotatingFileLogSink failed rotating ", fault.Message, StringComparison.Ordinal);
            Assert.Equal(1, sink.DroppedCount);
        }

        // The file was closed before the failed rename, so the line before the failure is kept.
        Assert.Equal(Line(Message(0, 40_000)), Read("engine.log"));
    }

    [Fact]
    public void Write_AfterFault_StaysFaultedWhenTheCauseIsGone()
    {
        using (RotatingFileLogSink sink = new(Options() with { MaxFileBytes = SmallestMax, MaxFiles = 2 }))
        {
            BlockArchiveOne();
            Write(sink, LogLevel.Information, Message(0, 40_000));
            Write(sink, LogLevel.Information, Message(1, 40_000));
            Directory.Delete(PathOf("engine.1.log"), recursive: true);

            Write(sink, LogLevel.Information, "next");

            Assert.Equal(2, sink.DroppedCount);
        }

        Assert.Equal(["engine.log"], Files(_root));
        Assert.Equal(Line(Message(0, 40_000)), Read("engine.log"));
    }

    [Fact]
    public void Write_StreamWriteFails_FaultsCountsTheLineAndClosesTheFile()
    {
        FailingFileStream? file = null;
        using RotatingFileLogSink sink = new(Options(), path => file = new FailingFileStream(path));
        file!.FailWrites = true;

        Write(sink, LogLevel.Information, "lost");

        EngineError fault = Assert.NotNull(sink.Fault);
        Assert.Equal(ErrorKind.Io, fault.Kind);
        Assert.StartsWith("RotatingFileLogSink failed writing ", fault.Message, StringComparison.Ordinal);
        Assert.Equal(1, sink.DroppedCount);
        Assert.True(file.IsDisposed);
    }

    [Fact]
    public void Write_FlushAtFlushLevelFails_FaultsAndCountsTheLine()
    {
        FailingFileStream? file = null;
        using RotatingFileLogSink sink = new(Options(), path => file = new FailingFileStream(path));
        file!.FailFlushes = true;

        Write(sink, LogLevel.Information, "buffered");
        Assert.Null(sink.Fault);

        Write(sink, LogLevel.Warning, "flush fails");

        Assert.Equal(ErrorKind.Io, Assert.NotNull(sink.Fault).Kind);
        Assert.Equal(1, sink.DroppedCount);
        Assert.True(file.IsDisposed);
    }

    [Fact]
    public void Flush_StreamFlushFails_FaultsWithoutThrowing()
    {
        FailingFileStream? file = null;
        using RotatingFileLogSink sink = new(Options(), path => file = new FailingFileStream(path));
        file!.FailFlushes = true;

        sink.Flush();

        Assert.Equal(ErrorKind.Io, Assert.NotNull(sink.Fault).Kind);
        Assert.Equal(0, sink.DroppedCount);
        Assert.True(file.IsDisposed);
    }

    [Fact]
    public void Dispose_FinalFlushFails_FaultsWithoutThrowingAndClosesTheFile()
    {
        FailingFileStream? file = null;
        RotatingFileLogSink sink = new(Options(), path => file = new FailingFileStream(path));
        file!.FailFlushes = true;

        sink.Dispose();

        Assert.Equal(ErrorKind.Io, Assert.NotNull(sink.Fault).Kind);
        Assert.True(file.IsDisposed);
    }

    [Fact]
    public void Write_FaultedThroughLogger_ThrowsNothingAndCountsNoSinkFailure()
    {
        using RotatingFileLogSink sink = new(Options() with { MaxFileBytes = SmallestMax, MaxFiles = 2 });
        BlockArchiveOne();
        MemoryLogSink other = new(capacityEntries: 4, capacityBytes: 128 * 1024);
        using Logger logger = new(new LoggerOptions { Sinks = [sink, other], TimeProvider = new FixedTimeProvider(DateTimeOffset.UnixEpoch), MaxMessageBytes = 65536 });
        LogCategory category = logger.CreateCategory("Core");
        string large = new('x', 40_000);

        category.Information($"{large}");
        category.Information($"{large}");
        category.Information($"{large}");
        logger.Flush();

        Assert.Equal(0, logger.SinkFailureCount);
        Assert.Equal(ErrorKind.Io, Assert.NotNull(sink.Fault).Kind);
        Assert.Equal(2, sink.DroppedCount);
        Assert.Equal(3, other.CopyTo(new MemoryLogRecord[4], new byte[128 * 1024]));
    }

    [Fact]
    public void Write_RotationAfterInterruptedShift_FaultsAndKeepsOlderArchives()
    {
        // Two 40,000-byte lines never share a 64 KiB file, so each write after the first rotates.
        const int LineBytes = 40_000;
        using (RotatingFileLogSink sink = new(Options() with { MaxFileBytes = SmallestMax, MaxFiles = 4 }))
        {
            for (int i = 0; i < 4; i++)
            {
                Write(sink, LogLevel.Information, Message(i, LineBytes));
            }

            // engine.log holds line 3 and archives 1 to 3 hold lines 2, 1 and 0. Replace archive 1 with a non-empty
            // directory: the state a rotation leaves when it shifted the archives but couldn't rename engine.log.
            File.Delete(PathOf("engine.1.log"));
            BlockArchiveOne();
            Write(sink, LogLevel.Information, Message(4, LineBytes));
            Write(sink, LogLevel.Information, Message(5, LineBytes));

            // The first write faulted the sink and the second was dropped: neither touched the older archives.
            Assert.Equal(ErrorKind.Io, Assert.NotNull(sink.Fault).Kind);
            Assert.Equal(2, sink.DroppedCount);
        }

        Assert.Equal(["engine.2.log", "engine.3.log", "engine.log"], Files(_root));
        Assert.Equal(Line(Message(1, LineBytes)), Read("engine.2.log"));
        Assert.Equal(Line(Message(0, LineBytes)), Read("engine.3.log"));
        Assert.Equal(Line(Message(3, LineBytes)), Read("engine.log"));
    }

    [Fact]
    public void Construct_AfterInterruptedShift_MovesNoArchiveAndKeepsAll()
    {
        const int LineBytes = 40_000;
        using (RotatingFileLogSink sink = new(Options() with { MaxFileBytes = SmallestMax, MaxFiles = 4 }))
        {
            for (int i = 0; i < 4; i++)
            {
                Write(sink, LogLevel.Information, Message(i, LineBytes));
            }
        }

        // The state a rotation leaves when it shifted the archives but couldn't rename engine.log: archive 1 is free.
        // Here engine.log and archives 2 and 3 hold lines 3, 1 and 0.
        File.Delete(PathOf("engine.1.log"));

        // The next run's startup finds archive 1 free, so it moves only engine.log: no archive ages a second time.
        new RotatingFileLogSink(Options() with { MaxFileBytes = SmallestMax, MaxFiles = 4 }).Dispose();

        Assert.Equal(["engine.1.log", "engine.2.log", "engine.3.log", "engine.log"], Files(_root));
        Assert.Equal(Line(Message(3, LineBytes)), Read("engine.1.log"));
        Assert.Equal(Line(Message(1, LineBytes)), Read("engine.2.log"));
        Assert.Equal(Line(Message(0, LineBytes)), Read("engine.3.log"));
        Assert.Equal(string.Empty, Read("engine.log"));
    }

    /// <summary>
    /// Deletes a test directory. Cleanup never fails a test: on Windows an antivirus scanner or the indexer can
    /// briefly hold a file, and a leftover directory under the temp path is harmless.
    /// </summary>
    private static void DeleteBestEffort(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best effort, see above.
        }
    }

    private static void Capture(ConcurrentQueue<Exception> failures, Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            failures.Enqueue(exception);
        }
    }

    private static void Write(RotatingFileLogSink sink, LogLevel level, string message)
    {
        LogEntry entry = new() { Level = level, Category = "Core", Timestamp = DateTimeOffset.UnixEpoch, Message = Encoding.UTF8.GetBytes(message) };
        sink.Write(entry);
    }

    private static string Line(string message, string level = "INFO") => $"{Prefix} {level} [Core] {message}\n";

    /// <summary>A unique ASCII message whose INFO line is exactly <paramref name="lineBytes"/> bytes long.</summary>
    private static string Message(int index, int lineBytes) =>
        $"{index:D6}:" + new string((char)('a' + (index % 26)), lineBytes - LineOverhead - 7);

    private static string[] Files(string directory) =>
        [.. Directory.GetFiles(directory).Select(path => Path.GetFileName(path)).Order(StringComparer.Ordinal)];

    private RotatingFileLogSinkOptions Options() => new() { Directory = _root };

    private string PathOf(string name) => Path.Combine(_root, name);

    /// <summary>Reads a file in the test directory, or at an absolute path, as UTF-8.</summary>
    private string Read(string nameOrPath)
    {
        // Shares everything, so the sink's own handle (FileShare.Read, writing) doesn't block the read.
        using FileStream stream = new(Path.Combine(_root, nameOrPath), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using StreamReader reader = new(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Puts a non-empty directory where the next rotation must move the current file, so that rotation fails on
    /// every OS, whatever the user's permissions (root and Windows administrators included).
    /// </summary>
    private void BlockArchiveOne() => Directory.CreateDirectory(Path.Combine(_root, "engine.1.log", "occupied"));

    /// <summary>
    /// A real log file (opened as the sink opens it) whose writes or flushes fail on demand, as a full disk would.
    /// Its final flush on dispose fails too when <see cref="FailFlushes"/> is set, after closing the file.
    /// </summary>
    private sealed class FailingFileStream(string path) : Stream
    {
        private readonly FileStream _inner = new(path, FileMode.Create, FileAccess.Write, FileShare.Read);

        public bool FailWrites { get; set; }

        public bool FailFlushes { get; set; }

        public bool IsDisposed { get; private set; }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => !IsDisposed;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            if (FailWrites)
            {
                throw new IOException("Disk full (test).");
            }

            _inner.Write(buffer);
        }

        public override void Flush()
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            if (FailFlushes)
            {
                throw new IOException("Flush failed (test).");
            }

            _inner.Flush();
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            try
            {
                if (disposing && !IsDisposed)
                {
                    IsDisposed = true;
                    _inner.Dispose();
                    if (FailFlushes)
                    {
                        throw new IOException("Final flush failed (test).");
                    }
                }
            }
            finally
            {
                base.Dispose(disposing);
            }
        }
    }
}
