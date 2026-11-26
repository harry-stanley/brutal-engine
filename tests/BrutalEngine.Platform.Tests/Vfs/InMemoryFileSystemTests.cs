using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using BrutalEngine.Core;
using BrutalEngine.Platform.Vfs;
using Xunit;

namespace BrutalEngine.Platform.Tests.Vfs;

public sealed class InMemoryFileSystemTests
{
    // CJK ideograph U+65E5: 3 bytes in UTF-8, one UTF-16 code unit.
    private const char Cjk = '\u65E5';

    [Fact]
    public void WriteThenRead_RoundTrips()
    {
        InMemoryFileSystem fileSystem = new();
        byte[] contents = [1, 2, 3, 0, 255];

        Assert.True(fileSystem.WriteAllBytes("textures/ui/button.png", contents).IsSuccess);

        Assert.Equal(contents, fileSystem.ReadAllBytes("textures/ui/button.png").Value);
        using (Stream stream = fileSystem.OpenRead("textures/ui/button.png").Value)
        {
            Assert.True(stream.CanRead);
            Assert.False(stream.CanWrite);
            Assert.Equal(0, stream.Position);
            Assert.Equal(contents, ReadToEnd(stream));
        }

        Assert.Equal(VfsEntryKind.File, fileSystem.GetEntryKind("textures/ui/button.png").Value);
        Assert.Equal(VfsEntryKind.Directory, fileSystem.GetEntryKind("textures/ui").Value);
        Assert.Equal(VfsEntryKind.Directory, fileSystem.GetEntryKind("textures").Value);
        Assert.Equal(VfsEntryKind.Directory, fileSystem.GetEntryKind(string.Empty).Value);
        Assert.Equal(VfsEntryKind.None, fileSystem.GetEntryKind("textures/ui/missing.png").Value);

        // A second write replaces the whole file, and an empty file is a file.
        Assert.True(fileSystem.WriteAllBytes("textures/ui/button.png", [9]).IsSuccess);
        Assert.Equal([9], fileSystem.ReadAllBytes("textures/ui/button.png").Value);
        Assert.True(fileSystem.WriteAllBytes("empty.bin", ReadOnlySpan<byte>.Empty).IsSuccess);
        Assert.Empty(fileSystem.ReadAllBytes("empty.bin").Value);
        Assert.Equal(VfsEntryKind.File, fileSystem.GetEntryKind("empty.bin").Value);
    }

    [Fact]
    public void Write_CopiesCallerBytes()
    {
        InMemoryFileSystem fileSystem = new();
        byte[] contents = [1, 2, 3];

        Assert.True(fileSystem.WriteAllBytes("a.bin", contents).IsSuccess);
        contents[0] = 42;
        Assert.Equal([1, 2, 3], fileSystem.ReadAllBytes("a.bin").Value);

        // Each read returns its own copy, so a caller changing one changes neither the file nor the next read.
        byte[] first = fileSystem.ReadAllBytes("a.bin").Value;
        first[1] = 42;
        byte[] second = fileSystem.ReadAllBytes("a.bin").Value;
        Assert.Equal([1, 2, 3], second);
        Assert.NotSame(first, second);
    }

    [Fact]
    public void OpenRead_IsReadOnlyCopy_UnaffectedByLaterWrites()
    {
        InMemoryFileSystem fileSystem = new();
        Assert.True(fileSystem.WriteAllBytes("a.bin", [1, 2, 3]).IsSuccess);

        using Stream stream = fileSystem.OpenRead("a.bin").Value;
        Assert.True(fileSystem.WriteAllBytes("a.bin", [7, 7, 7, 7]).IsSuccess);

        Assert.Equal([1, 2, 3], ReadToEnd(stream));
        Assert.Throws<NotSupportedException>(() => stream.WriteByte(0));
        Assert.Throws<NotSupportedException>(() => stream.SetLength(0));
        Assert.False(stream is MemoryStream memory && memory.TryGetBuffer(out _));
        Assert.Equal([7, 7, 7, 7], fileSystem.ReadAllBytes("a.bin").Value);
    }

    [Theory]
    [InlineData("WriteAllBytes")]
    [InlineData("WriteAllBytes, new file")]
    [InlineData("Delete")]
    [InlineData("Delete, root")]
    [InlineData("CreateDirectory")]
    [InlineData("CreateDirectory, existing")]
    [InlineData("CreateDirectory, root")]
    public void Freeze_MakesWritesUnsupported(string operation)
    {
        InMemoryFileSystem fileSystem = new();
        Seed(fileSystem);
        Assert.False(fileSystem.IsReadOnly);

        fileSystem.Freeze();
        fileSystem.Freeze();

        Assert.True(fileSystem.IsReadOnly);
        Result result = RunWrite(fileSystem, operation);
        AssertFailure(result.IsFailure, result.IsFailure ? result.Error : default, ErrorKind.Unsupported);
        AssertSeedUnchanged(fileSystem);
    }

    [Theory]
    [InlineData("WriteAllBytes")]
    [InlineData("Delete")]
    [InlineData("CreateDirectory")]
    [InlineData("CreateDirectory, root")]
    public void Constructor_ReadOnly_RefusesEveryWrite(string operation)
    {
        InMemoryFileSystem fileSystem = new(isReadOnly: true);

        Assert.True(fileSystem.IsReadOnly);
        Result result = RunWrite(fileSystem, operation);
        AssertFailure(result.IsFailure, result.IsFailure ? result.Error : default, ErrorKind.Unsupported);
        Assert.Empty(fileSystem.List(string.Empty).Value);
        Assert.Equal(VfsEntryKind.None, fileSystem.GetEntryKind("new.txt").Value);
    }

    [Theory]
    [InlineData(false, "WriteAllBytes", "", ErrorKind.NotFound)]
    [InlineData(false, "WriteAllBytes", "dir", ErrorKind.NotFound)]
    [InlineData(false, "WriteAllBytes", "file.txt/child.txt", ErrorKind.NotFound)]
    [InlineData(false, "CreateDirectory", "file.txt", ErrorKind.NotFound)]
    [InlineData(false, "CreateDirectory", "file.txt/child", ErrorKind.NotFound)]
    [InlineData(false, "Delete", "missing.txt", ErrorKind.NotFound)]
    [InlineData(false, "Delete", "dir", ErrorKind.Io)]
    [InlineData(true, "WriteAllBytes", "", ErrorKind.NotFound)]
    [InlineData(true, "Delete", "missing.txt", ErrorKind.NotFound)]
    [InlineData(true, "Delete", "dir/missing.txt", ErrorKind.NotFound)]
    public void ReadOnly_IsCheckedBeforeEveryOtherFailure(bool fromConstructor, string operation, string path, ErrorKind otherwise)
    {
        // Each path fails another way on a writable twin (proved first), so an implementation that looks at the
        // path before the read-only flag answers that kind instead of Unsupported. A file system created read-only
        // stays empty, so only the root write and the missing-entry deletes can be built for it; the root is its one
        // existing directory, and nothing in it can be a file.
        InMemoryFileSystem writable = new();
        if (!fromConstructor)
        {
            Seed(writable);
        }

        Result twin = RunWrite(writable, operation, path);
        AssertFailure(twin.IsFailure, twin.IsFailure ? twin.Error : default, otherwise);

        InMemoryFileSystem fileSystem;
        if (fromConstructor)
        {
            fileSystem = new(isReadOnly: true);
        }
        else
        {
            fileSystem = new();
            Seed(fileSystem);
            fileSystem.Freeze();
        }

        Result result = RunWrite(fileSystem, operation, path);

        AssertFailure(result.IsFailure, result.IsFailure ? result.Error : default, ErrorKind.Unsupported);
        Assert.Contains($"'{path}'", result.Error.Message, StringComparison.Ordinal);
        if (fromConstructor)
        {
            Assert.Empty(fileSystem.List(string.Empty).Value);
        }
        else
        {
            AssertSeedUnchanged(fileSystem);
        }
    }

    [Fact]
    public void Freeze_KeepsReadsWorking()
    {
        InMemoryFileSystem fileSystem = new();
        Seed(fileSystem);

        fileSystem.Freeze();

        AssertSeedUnchanged(fileSystem);
        using Stream stream = fileSystem.OpenRead("file.txt").Value;
        Assert.Equal("abc"u8.ToArray(), ReadToEnd(stream));
    }

    [Fact]
    public void Delete_NonEmptyDirectory_IsIo()
    {
        InMemoryFileSystem fileSystem = new();
        Seed(fileSystem);

        Result result = fileSystem.Delete("dir");

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorKind.Io, result.Error.Kind);
        Assert.Equal(0, result.Error.NativeCode);
        Assert.Contains("'dir'", result.Error.Message, StringComparison.Ordinal);
        Assert.Equal(VfsEntryKind.File, fileSystem.GetEntryKind("dir/inner.txt").Value);

        // Once it is empty, it can go.
        Assert.True(fileSystem.Delete("dir/inner.txt").IsSuccess);
        Assert.True(fileSystem.Delete("dir").IsSuccess);
        Assert.Equal(VfsEntryKind.None, fileSystem.GetEntryKind("dir").Value);
    }

    [Fact]
    public void Delete_FileOrEmptyDirectory_RemovesItFromItsParent()
    {
        InMemoryFileSystem fileSystem = new();
        Seed(fileSystem);

        Assert.True(fileSystem.Delete("file.txt").IsSuccess);
        Assert.True(fileSystem.Delete("empty").IsSuccess);

        Assert.Equal(VfsEntryKind.None, fileSystem.GetEntryKind("file.txt").Value);
        Assert.Equal(VfsEntryKind.None, fileSystem.GetEntryKind("empty").Value);
        Assert.Equal([new VfsEntry("dir", VfsEntryKind.Directory, 0)], fileSystem.List(string.Empty).Value);
        Assert.True(fileSystem.ReadAllBytes("file.txt").IsFailure);
    }

    [Fact]
    public void List_IsSortedOrdinally()
    {
        InMemoryFileSystem fileSystem = new();

        // Written in an order unrelated to the expected one. The expected order below is UTF-16 code-unit order,
        // worked out by hand: '1' (U+0031) < '9' < 'B' (U+0042) < '_' (U+005F) < 'a' (U+0061) < 'e' < U+00E9 'é'
        // < U+65E5 < U+D83D (the high surrogate of U+1F600) < U+FF21 (fullwidth A). The last pair is where UTF-16
        // order differs from code-point order, and every culture-aware sort puts 'a' next to 'B'.
        string[] names = ["\uFF21", "a", "B", "\U0001F600", "_", "9", "e\u0301", "\u00E9", "10", Cjk.ToString(), "a.txt"];
        foreach (string name in names)
        {
            Assert.True(fileSystem.WriteAllBytes("list/" + name, new byte[name.Length]).IsSuccess);
        }

        Assert.True(fileSystem.CreateDirectory("list/dir").IsSuccess);
        Assert.True(fileSystem.WriteAllBytes("list/sub/deeper.bin", [1]).IsSuccess);

        IReadOnlyList<VfsEntry> entries = fileSystem.List("list").Value;

        VfsEntry[] expected =
        [
            new("10", VfsEntryKind.File, 2),
            new("9", VfsEntryKind.File, 1),
            new("B", VfsEntryKind.File, 1),
            new("_", VfsEntryKind.File, 1),
            new("a", VfsEntryKind.File, 1),
            new("a.txt", VfsEntryKind.File, 5),
            new("dir", VfsEntryKind.Directory, 0),
            new("e\u0301", VfsEntryKind.File, 2),
            new("sub", VfsEntryKind.Directory, 0),
            new("\u00E9", VfsEntryKind.File, 1),
            new(Cjk.ToString(), VfsEntryKind.File, 1),
            new("\U0001F600", VfsEntryKind.File, 2),
            new("\uFF21", VfsEntryKind.File, 1),
        ];
        Assert.Equal(expected, entries);
        Assert.Equal([new VfsEntry("list", VfsEntryKind.Directory, 0)], fileSystem.List(string.Empty).Value);
        Assert.Empty(fileSystem.List("list/dir").Value);
    }

    [Fact]
    public void List_ReturnsANewListEachCall()
    {
        InMemoryFileSystem fileSystem = new();
        Seed(fileSystem);

        IReadOnlyList<VfsEntry> before = fileSystem.List(string.Empty).Value;
        Assert.True(fileSystem.WriteAllBytes("zzz.txt", [1]).IsSuccess);
        IReadOnlyList<VfsEntry> after = fileSystem.List(string.Empty).Value;

        Assert.Equal(3, before.Count);
        Assert.Equal(4, after.Count);
        Assert.NotSame(before, after);
    }

    [Fact]
    public void CreateDirectory_CreatesParentsAndIsIdempotent()
    {
        InMemoryFileSystem fileSystem = new();

        Assert.True(fileSystem.CreateDirectory("a/b/c").IsSuccess);
        Assert.True(fileSystem.CreateDirectory("a/b/c").IsSuccess);
        Assert.True(fileSystem.CreateDirectory("a").IsSuccess);
        Assert.True(fileSystem.CreateDirectory(string.Empty).IsSuccess);

        Assert.Equal(VfsEntryKind.Directory, fileSystem.GetEntryKind("a").Value);
        Assert.Equal(VfsEntryKind.Directory, fileSystem.GetEntryKind("a/b").Value);
        Assert.Equal(VfsEntryKind.Directory, fileSystem.GetEntryKind("a/b/c").Value);
        Assert.Equal([new VfsEntry("b", VfsEntryKind.Directory, 0)], fileSystem.List("a").Value);
        Assert.Equal([new VfsEntry("a", VfsEntryKind.Directory, 0)], fileSystem.List(string.Empty).Value);
    }

    [Theory]
    [InlineData("ReadAllBytes", "missing.txt", ErrorKind.NotFound)]
    [InlineData("ReadAllBytes", "dir/missing.txt", ErrorKind.NotFound)]
    [InlineData("ReadAllBytes", "file.txt/child", ErrorKind.NotFound)]
    [InlineData("ReadAllBytes", "dir", ErrorKind.NotFound)]
    [InlineData("ReadAllBytes", "", ErrorKind.NotFound)]
    [InlineData("OpenRead", "missing.txt", ErrorKind.NotFound)]
    [InlineData("OpenRead", "dir", ErrorKind.NotFound)]
    [InlineData("OpenRead", "", ErrorKind.NotFound)]
    [InlineData("List", "missing", ErrorKind.NotFound)]
    [InlineData("List", "file.txt", ErrorKind.NotFound)]
    [InlineData("WriteAllBytes", "dir", ErrorKind.NotFound)]
    [InlineData("WriteAllBytes", "", ErrorKind.NotFound)]
    [InlineData("WriteAllBytes", "file.txt/child.txt", ErrorKind.NotFound)]
    [InlineData("WriteAllBytes", "dir/inner.txt/deeper/new.txt", ErrorKind.NotFound)]
    [InlineData("Delete", "missing.txt", ErrorKind.NotFound)]
    [InlineData("Delete", "file.txt/child", ErrorKind.NotFound)]
    [InlineData("Delete", "", ErrorKind.Unsupported)]
    [InlineData("Delete", "dir", ErrorKind.Io)]
    [InlineData("CreateDirectory", "file.txt", ErrorKind.NotFound)]
    [InlineData("CreateDirectory", "file.txt/child", ErrorKind.NotFound)]
    [InlineData("CreateDirectory", "dir/inner.txt/deeper/new", ErrorKind.NotFound)]
    public void ErrorRows(string operation, string path, ErrorKind expected)
    {
        InMemoryFileSystem fileSystem = new();
        Seed(fileSystem);

        (bool failed, EngineError error) = operation switch
        {
            "ReadAllBytes" => Outcome(fileSystem.ReadAllBytes(path)),
            "OpenRead" => Outcome(fileSystem.OpenRead(path)),
            "List" => Outcome(fileSystem.List(path)),
            "WriteAllBytes" => Outcome(fileSystem.WriteAllBytes(path, [1, 2])),
            "Delete" => Outcome(fileSystem.Delete(path)),
            "CreateDirectory" => Outcome(fileSystem.CreateDirectory(path)),
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unknown operation."),
        };

        AssertFailure(failed, error, expected);
        Assert.Equal(0, error.NativeCode);
        Assert.Contains($"'{path}'", error.Message, StringComparison.Ordinal);

        // A failed operation changes nothing: no parent directory is left behind either.
        AssertSeedUnchanged(fileSystem);
        Assert.Equal(VfsEntryKind.None, fileSystem.GetEntryKind("dir/inner.txt/deeper").Value);
    }

    [Fact]
    public void GetEntryKind_Missing_IsSuccessWithNone()
    {
        InMemoryFileSystem fileSystem = new();
        Seed(fileSystem);

        foreach (string path in new[] { "missing", "dir/missing", "file.txt/child", "missing/deeper" })
        {
            Result<VfsEntryKind> kind = fileSystem.GetEntryKind(path);
            Assert.True(kind.IsSuccess, path);
            Assert.Equal(VfsEntryKind.None, kind.Value);
        }
    }

    [Theory]
    [InlineData("GetEntryKind")]
    [InlineData("ReadAllBytes")]
    [InlineData("OpenRead")]
    [InlineData("WriteAllBytes")]
    [InlineData("Delete")]
    [InlineData("CreateDirectory")]
    [InlineData("List")]
    public void NullPath_Throws(string operation)
    {
        // Frozen, so a misordered check that answers Unsupported before looking at the argument fails too.
        InMemoryFileSystem fileSystem = new();
        fileSystem.Freeze();

        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(() => Run(fileSystem, operation, null!));

        Assert.Equal("relativePath", exception.ParamName);
    }

    public static TheoryData<string, string> NotNormalisedPaths()
    {
        TheoryData<string, string> rows = [];
        string[] paths =
        [
            "/a", "a/", "a//b", ".", "./a", "a/.", "a/./b", "..", "a/..", "a/../b", "a\\b", "a:b", "a*b", "a\u0000b",
            "a.", "a ", "CON", "dir/nul.txt", "x://a", "\uD800", new string(Cjk, 86), new string('a', 256),
            new string('a', 1021), "/",
        ];
        foreach (string operation in new[] { "GetEntryKind", "ReadAllBytes", "OpenRead", "WriteAllBytes", "Delete", "CreateDirectory", "List" })
        {
            foreach (string path in paths)
            {
                rows.Add(operation, path);
            }
        }

        return rows;
    }

    [Theory]
    [MemberData(nameof(NotNormalisedPaths))]
    public void NotNormalisedPath_Throws(string operation, string path)
    {
        InMemoryFileSystem fileSystem = new();
        fileSystem.Freeze();

        ArgumentException exception = Assert.Throws<ArgumentException>(() => Run(fileSystem, operation, path));

        Assert.Equal("relativePath", exception.ParamName);

        // The message describes the rule and doesn't echo the input, so a 1,021-character path can't bloat it.
        Assert.InRange(exception.Message.Length, 1, 400);
    }

    [Theory]
    [InlineData("a..b")]
    [InlineData(".hidden")]
    [InlineData("a b/ c")]
    [InlineData("...a")]
    [InlineData("CONSOLE/COM10/nul_")]
    [InlineData("\u65E5\u672C/\u30D5\u30A1\u30A4\u30EB.png")]
    [InlineData("\U0001F600")]
    public void LookAlikeButValidPath_IsAccepted(string path)
    {
        InMemoryFileSystem fileSystem = new();

        Assert.True(fileSystem.WriteAllBytes(path, [1]).IsSuccess);
        Assert.Equal([1], fileSystem.ReadAllBytes(path).Value);
    }

    [Theory]
    [InlineData(85, true)]
    [InlineData(86, false)]
    public void SegmentLimit_CountsUtf8Bytes(int ideographs, bool valid)
    {
        // 85 ideographs are 255 UTF-8 bytes (the limit) and 86 are 258, though both are under 255 UTF-16 code units.
        InMemoryFileSystem fileSystem = new();
        string path = new(Cjk, ideographs);

        if (valid)
        {
            Assert.True(fileSystem.WriteAllBytes(path, [1]).IsSuccess);
        }
        else
        {
            Assert.Throws<ArgumentException>(() => fileSystem.WriteAllBytes(path, [1]));
        }
    }

    [Theory]
    [InlineData(254, true)]
    [InlineData(255, true)]
    [InlineData(256, false)]
    public void SegmentLimit_Ascii_AtTheBoundary(int length, bool valid)
    {
        InMemoryFileSystem fileSystem = new();
        string path = new('a', length);

        Assert.Equal(valid ? VfsEntryKind.None : (VfsEntryKind?)null, TryKind(fileSystem, path));
    }

    [Theory]
    [InlineData(1019, true)]
    [InlineData(1020, true)]
    [InlineData(1021, false)]
    public void PathLimit_IsWhatTheShortestSchemeLeaves(int bytes, bool valid)
    {
        // The whole path is at most 1,024 UTF-8 bytes, and the shortest prefix a path can have is "r://" (4 bytes),
        // so 1,020 bytes is the longest relative path any VfsPath has. Segments of 200 bytes joined by '/'.
        InMemoryFileSystem fileSystem = new();
        string path = BuildPath(bytes);
        Assert.Equal(bytes, System.Text.Encoding.UTF8.GetByteCount(path));

        Assert.Equal(valid ? VfsEntryKind.None : (VfsEntryKind?)null, TryKind(fileSystem, path));
    }

    [Fact]
    public void ConcurrentWritersAndReaders_StayConsistent()
    {
        // Every writer replaces one shared file with arrays whose bytes all equal the writer's id and whose length
        // is id + 1, and keeps its own files under its own directory. Readers check every read is one whole write
        // (never a mix) and every listing, of the root and of a writer's directory, is sorted and well formed.
        // Threads start together through a barrier.
        const int Writers = 4;
        const int Readers = 4;
        const int Iterations = 2_000;
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        InMemoryFileSystem fileSystem = new();
        Assert.True(fileSystem.WriteAllBytes("shared.bin", [0]).IsSuccess);
        ConcurrentQueue<Exception> failures = new();
        using Barrier barrier = new(Writers + Readers);
        List<Thread> threads = [];

        for (int w = 0; w < Writers; w++)
        {
            byte id = (byte)(w + 1);
            threads.Add(new Thread(() => Guard(failures, () =>
            {
                barrier.SignalAndWait(cancellation);
                byte[] own = new byte[id + 1];
                Array.Fill(own, id);
                for (int i = 0; i < Iterations; i++)
                {
                    Check(fileSystem.WriteAllBytes("shared.bin", own).IsSuccess, "shared write");
                    Check(fileSystem.WriteAllBytes($"w{id}/f{i % 16}.bin", own).IsSuccess, "own write");
                    if (i % 4 == 3)
                    {
                        Check(fileSystem.Delete($"w{id}/f{i % 16}.bin").IsSuccess, "own delete");
                    }
                }
            })));
        }

        for (int r = 0; r < Readers; r++)
        {
            threads.Add(new Thread(() => Guard(failures, () =>
            {
                barrier.SignalAndWait(cancellation);
                for (int i = 0; i < Iterations; i++)
                {
                    byte[] bytes = fileSystem.ReadAllBytes("shared.bin").Value;
                    CheckWhole(bytes);
                    using (Stream stream = fileSystem.OpenRead("shared.bin").Value)
                    {
                        CheckWhole(ReadToEnd(stream));
                    }

                    CheckListing(fileSystem.List(string.Empty).Value);

                    // A writer's own directory changes on every iteration; it doesn't exist until its first write.
                    Result<IReadOnlyList<VfsEntry>> own = fileSystem.List($"w{(i % Writers) + 1}");
                    Check(own.IsSuccess || own.Error.Kind == ErrorKind.NotFound, "own listing failed");
                    if (own.IsSuccess)
                    {
                        CheckListing(own.Value);
                    }
                }
            })));
        }

        StartAndJoin(threads);

        Assert.Empty(failures);
        for (int w = 1; w <= Writers; w++)
        {
            // Iterations is a multiple of 16, so the last write to f0..f15 decides each: deleted when i % 4 == 3.
            for (int f = 0; f < 16; f++)
            {
                VfsEntryKind expected = f % 4 == 3 ? VfsEntryKind.None : VfsEntryKind.File;
                Assert.Equal(expected, fileSystem.GetEntryKind($"w{w}/f{f}.bin").Value);
            }

            Assert.Equal(12, fileSystem.List($"w{w}").Value.Count);
        }

        CheckWhole(fileSystem.ReadAllBytes("shared.bin").Value);
    }

    [Fact]
    public void ConcurrentSameLengthWrites_NeverTearReads()
    {
        // Writers replace one file with 64 KiB arrays of the same length, each filled with a 32-bit stamp unique to
        // that write (writer and iteration), so the only way to tell two writes apart is their contents. Readers read
        // through ReadAllBytes and OpenRead and check each result holds a single stamp: copying a new write into the
        // stored array in place, while a reader copies it outside the lock, shows up as a mix of two stamps. Threads
        // start together through a barrier, and writers keep writing until every reader has finished, so every read
        // overlaps the writes; the writers' loop is capped too, so a stuck reader fails the join instead of a hang.
        const int Writers = 2;
        const int Readers = 2;
        const int ReadIterations = 500;
        const int WriterCap = 1_000_000;
        const int Length = 64 * 1024;
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        InMemoryFileSystem fileSystem = new();
        Assert.True(fileSystem.WriteAllBytes("shared.bin", new byte[Length]).IsSuccess);
        ConcurrentQueue<Exception> failures = new();
        using Barrier barrier = new(Writers + Readers);
        int readersLeft = Readers;
        List<Thread> threads = [];

        for (int w = 0; w < Writers; w++)
        {
            int writer = w + 1;
            threads.Add(new Thread(() => Guard(failures, () =>
            {
                barrier.SignalAndWait(cancellation);
                byte[] write = new byte[Length];
                for (int i = 1; i == 1 || (i <= WriterCap && Volatile.Read(ref readersLeft) > 0); i++)
                {
                    MemoryMarshal.Cast<byte, int>(write.AsSpan()).Fill((writer << 24) | i);
                    Check(fileSystem.WriteAllBytes("shared.bin", write).IsSuccess, "shared write");
                }
            })));
        }

        for (int r = 0; r < Readers; r++)
        {
            threads.Add(new Thread(() => Guard(failures, () =>
            {
                try
                {
                    barrier.SignalAndWait(cancellation);
                    for (int i = 0; i < ReadIterations; i++)
                    {
                        CheckSingleStamp(fileSystem.ReadAllBytes("shared.bin").Value, Length);
                        using Stream stream = fileSystem.OpenRead("shared.bin").Value;
                        CheckSingleStamp(ReadToEnd(stream), Length);
                    }
                }
                finally
                {
                    Interlocked.Decrement(ref readersLeft);
                }
            })));
        }

        StartAndJoin(threads);

        Assert.Empty(failures);

        // What remains is one whole write by one of the writers.
        byte[] last = fileSystem.ReadAllBytes("shared.bin").Value;
        CheckSingleStamp(last, Length);
        Assert.InRange(MemoryMarshal.Read<int>(last) >> 24, 1, Writers);
    }

    private static void StartAndJoin(List<Thread> threads)
    {
        // Background threads, so a regression that deadlocks the lock fails the bounded join below without the
        // stuck workers keeping the test host alive.
        foreach (Thread thread in threads)
        {
            thread.IsBackground = true;
            thread.Start();
        }

        foreach (Thread thread in threads)
        {
            Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "A worker thread did not finish.");
        }
    }

    private static void CheckSingleStamp(byte[] bytes, int length)
    {
        Check(bytes.Length == length, $"length {bytes.Length}, expected {length}");
        ReadOnlySpan<int> stamps = MemoryMarshal.Cast<byte, int>(bytes);
        int first = stamps[0];
        foreach (int stamp in stamps)
        {
            Check(stamp == first, $"torn read: stamps {first:X8} and {stamp:X8} in one result");
        }
    }

    private static void Seed(InMemoryFileSystem fileSystem)
    {
        // file.txt (3 bytes), dir/inner.txt (2 bytes) and an empty directory, empty.
        Assert.True(fileSystem.WriteAllBytes("file.txt", "abc"u8).IsSuccess);
        Assert.True(fileSystem.WriteAllBytes("dir/inner.txt", "xy"u8).IsSuccess);
        Assert.True(fileSystem.CreateDirectory("empty").IsSuccess);
    }

    private static void AssertSeedUnchanged(InMemoryFileSystem fileSystem)
    {
        VfsEntry[] root =
        [
            new("dir", VfsEntryKind.Directory, 0),
            new("empty", VfsEntryKind.Directory, 0),
            new("file.txt", VfsEntryKind.File, 3),
        ];
        Assert.Equal(root, fileSystem.List(string.Empty).Value);
        Assert.Equal([new VfsEntry("inner.txt", VfsEntryKind.File, 2)], fileSystem.List("dir").Value);
        Assert.Empty(fileSystem.List("empty").Value);
        Assert.Equal("abc"u8.ToArray(), fileSystem.ReadAllBytes("file.txt").Value);
        Assert.Equal("xy"u8.ToArray(), fileSystem.ReadAllBytes("dir/inner.txt").Value);
    }

    private static Result RunWrite(InMemoryFileSystem fileSystem, string operation, string path) => operation switch
    {
        "WriteAllBytes" => fileSystem.WriteAllBytes(path, [9, 9]),
        "Delete" => fileSystem.Delete(path),
        "CreateDirectory" => fileSystem.CreateDirectory(path),
        _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unknown operation."),
    };

    private static Result RunWrite(InMemoryFileSystem fileSystem, string operation) => operation switch
    {
        "WriteAllBytes" => fileSystem.WriteAllBytes("file.txt", [9, 9]),
        "WriteAllBytes, new file" => fileSystem.WriteAllBytes("new/new.txt", [9, 9]),
        "Delete" => fileSystem.Delete("file.txt"),
        "Delete, root" => fileSystem.Delete(string.Empty),
        "CreateDirectory" => fileSystem.CreateDirectory("new/dir"),
        "CreateDirectory, existing" => fileSystem.CreateDirectory("dir"),
        "CreateDirectory, root" => fileSystem.CreateDirectory(string.Empty),
        _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unknown operation."),
    };

    private static object Run(InMemoryFileSystem fileSystem, string operation, string path) => operation switch
    {
        "GetEntryKind" => fileSystem.GetEntryKind(path),
        "ReadAllBytes" => fileSystem.ReadAllBytes(path),
        "OpenRead" => fileSystem.OpenRead(path),
        "WriteAllBytes" => fileSystem.WriteAllBytes(path, [1]),
        "Delete" => fileSystem.Delete(path),
        "CreateDirectory" => fileSystem.CreateDirectory(path),
        "List" => fileSystem.List(path),
        _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unknown operation."),
    };

    private static VfsEntryKind? TryKind(InMemoryFileSystem fileSystem, string path)
    {
        try
        {
            return fileSystem.GetEntryKind(path).Value;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static string BuildPath(int bytes)
    {
        System.Text.StringBuilder builder = new();
        while (builder.Length + 201 <= bytes)
        {
            builder.Append('s', 200).Append('/');
        }

        return builder.Append('t', bytes - builder.Length).ToString();
    }

    private static (bool Failed, EngineError Error) Outcome<T>(Result<T> result) =>
        (result.IsFailure, result.IsFailure ? result.Error : default);

    private static (bool Failed, EngineError Error) Outcome(Result result) =>
        (result.IsFailure, result.IsFailure ? result.Error : default);

    private static void AssertFailure(bool failed, EngineError error, ErrorKind expected)
    {
        Assert.True(failed, "The operation succeeded.");
        Assert.Equal(expected, error.Kind);
        Assert.NotEmpty(error.Message);
    }

    private static byte[] ReadToEnd(Stream stream)
    {
        using MemoryStream copy = new();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    private static void CheckWhole(byte[] bytes)
    {
        Check(bytes.Length >= 1, "empty read");
        byte id = bytes[0];
        Check(bytes.Length == (id == 0 ? 1 : id + 1), $"length {bytes.Length} doesn't match writer {id}");
        foreach (byte b in bytes)
        {
            Check(b == id, "torn read: bytes from two writes");
        }
    }

    private static void CheckListing(IReadOnlyList<VfsEntry> entries)
    {
        for (int e = 0; e < entries.Count; e++)
        {
            Check(e == 0 || string.CompareOrdinal(entries[e - 1].Name, entries[e].Name) < 0, "listing out of order");
            Check(entries[e].Kind != VfsEntryKind.None && entries[e].Name.Length != 0, "listing holds a default entry");
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void Guard(ConcurrentQueue<Exception> failures, Action body)
    {
        try
        {
            body();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            failures.Enqueue(exception);
        }
    }
}
