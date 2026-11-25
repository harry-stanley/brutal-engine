using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
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

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

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
            Directory.Delete(directory, recursive: true);
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
    public void Write_RotationFails_PropagatesTheIoException()
    {
        using RotatingFileLogSink sink = new(Options() with { MaxFileBytes = SmallestMax, MaxFiles = 2 });
        BlockArchiveOne();
        Write(sink, LogLevel.Information, Message(0, 40_000));

        Exception thrown = Assert.ThrowsAny<Exception>(() => Write(sink, LogLevel.Information, Message(1, 40_000)));

        Assert.True(thrown is IOException or UnauthorizedAccessException, thrown.ToString());
    }

    [Fact]
    public void Write_AfterFailedRotation_RetriesTheRotation()
    {
        using (RotatingFileLogSink sink = new(Options() with { MaxFileBytes = SmallestMax, MaxFiles = 2 }))
        {
            BlockArchiveOne();
            Write(sink, LogLevel.Information, Message(0, 40_000));
            _ = Assert.ThrowsAny<Exception>(() => Write(sink, LogLevel.Information, Message(1, 40_000)));
            Directory.Delete(PathOf("engine.1.log"), recursive: true);

            Write(sink, LogLevel.Information, "next");
        }

        Assert.Equal(["engine.1.log", "engine.log"], Files(_root));
        Assert.Equal(Line(Message(0, 40_000)), Read("engine.1.log"));
        Assert.Equal(Line("next"), Read("engine.log"));
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
}
