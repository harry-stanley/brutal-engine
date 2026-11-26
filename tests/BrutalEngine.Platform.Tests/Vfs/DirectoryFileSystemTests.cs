using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using BrutalEngine.Core;
using BrutalEngine.Platform.Vfs;
using Xunit;

namespace BrutalEngine.Platform.Tests.Vfs;

// Every test gets its own temporary root (a new instance per test), deleted in Dispose; nothing is shared.
public sealed partial class DirectoryFileSystemTests : IDisposable
{
    // The HResults .NET gives the exceptions on every OS: COR_E_DIRECTORYNOTFOUND, COR_E_FILENOTFOUND and
    // COR_E_UNAUTHORIZEDACCESS (E_ACCESSDENIED).
    private const int DirectoryNotFound = unchecked((int)0x80070003);
    private const int AccessDenied = unchecked((int)0x80070005);

    // CJK ideograph U+65E5: 3 bytes in UTF-8, one UTF-16 code unit.
    private const char Cjk = '\u65E5';

    private readonly string _root = Path.Join(Path.GetTempPath(), "be-dirfs-" + Guid.NewGuid().ToString("N"));

    public DirectoryFileSystemTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void WriteReadListDelete_RoundTrip()
    {
        DirectoryFileSystem fileSystem = Create();
        byte[] contents = [1, 2, 3, 0, 255];
        Assert.False(fileSystem.IsReadOnly);

        // Parent directories are created, and the bytes land at the joined path on disk.
        Assert.True(fileSystem.WriteAllBytes("textures/ui/button.png", contents).IsSuccess);
        Assert.Equal(contents, File.ReadAllBytes(Path.Join(_root, "textures", "ui", "button.png")));

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
        Assert.Equal(VfsEntryKind.Directory, fileSystem.GetEntryKind(string.Empty).Value);
        Assert.Equal(VfsEntryKind.None, fileSystem.GetEntryKind("textures/missing.png").Value);
        Assert.Equal(VfsEntryKind.None, fileSystem.GetEntryKind("textures/ui/button.png/child").Value);

        // A second write replaces the whole file; an empty file and a dot-file are files too.
        Assert.True(fileSystem.WriteAllBytes("textures/ui/button.png", [9]).IsSuccess);
        Assert.Equal([9], fileSystem.ReadAllBytes("textures/ui/button.png").Value);
        Assert.True(fileSystem.WriteAllBytes("empty.bin", ReadOnlySpan<byte>.Empty).IsSuccess);
        Assert.Empty(fileSystem.ReadAllBytes("empty.bin").Value);
        Assert.True(fileSystem.WriteAllBytes(".hidden", [1, 2]).IsSuccess);

        // Creating directories creates parents, and is idempotent, the root included.
        Assert.True(fileSystem.CreateDirectory("a/b/c").IsSuccess);
        Assert.True(fileSystem.CreateDirectory("a/b/c").IsSuccess);
        Assert.True(fileSystem.CreateDirectory("a").IsSuccess);
        Assert.True(fileSystem.CreateDirectory(string.Empty).IsSuccess);
        Assert.True(Directory.Exists(Path.Join(_root, "a", "b", "c")));

        VfsEntry[] expected =
        [
            new(".hidden", VfsEntryKind.File, 2),
            new("a", VfsEntryKind.Directory, 0),
            new("empty.bin", VfsEntryKind.File, 0),
            new("textures", VfsEntryKind.Directory, 0),
        ];
        Assert.Equal(expected, fileSystem.List(string.Empty).Value);
        Assert.Equal([new VfsEntry("button.png", VfsEntryKind.File, 1)], fileSystem.List("textures/ui").Value);
        Assert.Empty(fileSystem.List("a/b/c").Value);

        // A file, then the directory it emptied, can be deleted.
        Assert.True(fileSystem.Delete("textures/ui/button.png").IsSuccess);
        Assert.True(fileSystem.Delete("textures/ui").IsSuccess);
        Assert.False(File.Exists(Path.Join(_root, "textures", "ui", "button.png")));
        Assert.False(Directory.Exists(Path.Join(_root, "textures", "ui")));
        Assert.Equal(VfsEntryKind.None, fileSystem.GetEntryKind("textures/ui").Value);
        Assert.Empty(fileSystem.List("textures").Value);
        AssertNoTemporaryFiles();
    }

    [Fact]
    public void List_IsSortedOrdinally()
    {
        DirectoryFileSystem fileSystem = Create();

        // Written in an order unrelated to the expected one, which is UTF-16 code-unit order worked out by hand:
        // '1' < '9' < 'B' (U+0042) < '_' (U+005F) < 'a' (U+0061) < U+00E9 < U+65E5 < U+D83D (the high surrogate of
        // U+1F600) < U+FF21 (fullwidth A). The last pair is where UTF-16 order differs from code-point order, and
        // every culture-aware sort puts 'a' next to 'B'. No two names differ only in case, so case-insensitive file
        // systems hold them all.
        string[] names = ["\uFF21", "a", "B", "\U0001F600", "_", "9", "\u00E9", "10", Cjk.ToString(), "a.txt"];
        foreach (string name in names)
        {
            Assert.True(fileSystem.WriteAllBytes("list/" + name, new byte[name.Length]).IsSuccess);
        }

        Assert.True(fileSystem.CreateDirectory("list/dir").IsSuccess);
        Assert.True(fileSystem.WriteAllBytes("list/sub/deeper.bin", [1]).IsSuccess);

        VfsEntry[] expected =
        [
            new("10", VfsEntryKind.File, 2),
            new("9", VfsEntryKind.File, 1),
            new("B", VfsEntryKind.File, 1),
            new("_", VfsEntryKind.File, 1),
            new("a", VfsEntryKind.File, 1),
            new("a.txt", VfsEntryKind.File, 5),
            new("dir", VfsEntryKind.Directory, 0),
            new("sub", VfsEntryKind.Directory, 0),
            new("\u00E9", VfsEntryKind.File, 1),
            new(Cjk.ToString(), VfsEntryKind.File, 1),
            new("\U0001F600", VfsEntryKind.File, 2),
            new("\uFF21", VfsEntryKind.File, 1),
        ];
        Assert.Equal(expected, fileSystem.List("list").Value);
    }

    [Fact]
    public void List_ShowsTemporaryFilesAndReturnsANewListEachCall()
    {
        DirectoryFileSystem fileSystem = Create();
        File.WriteAllBytes(Path.Join(_root, ".0123456789abcdef.betmp"), [1]);

        IReadOnlyList<VfsEntry> first = fileSystem.List(string.Empty).Value;
        IReadOnlyList<VfsEntry> second = fileSystem.List(string.Empty).Value;

        // A leftover of an interrupted write is listed, because hiding the pattern would hide a real file too.
        Assert.Equal([new VfsEntry(".0123456789abcdef.betmp", VfsEntryKind.File, 1)], first);
        Assert.NotSame(first, second);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WriteAllBytes_FailureMidWrite_KeepsOldFile(bool targetExists)
    {
        const int diskFull = unchecked((int)0x80070070);
        string target = Path.Join(_root, "save", "slot.bin");
        if (targetExists)
        {
            Directory.CreateDirectory(Path.Join(_root, "save"));
            File.WriteAllBytes(target, [1, 2, 3]);
        }

        string? written = null;
        DirectoryFileSystem fileSystem = new(Options(), stream =>
        {
            // The new bytes have gone to a temporary file in the target's directory, not to the target.
            written = ((FileStream)stream).Name;
            Assert.Equal(6, stream.Length);
            throw new IOException("Simulated: the disk is full.", diskFull);
        });

        Result result = fileSystem.WriteAllBytes("save/slot.bin", [7, 7, 7, 7, 7, 7]);

        AssertFailure(result, ErrorKind.Io, diskFull, "'save/slot.bin'");
        Assert.Contains("Simulated: the disk is full.", result.Error.Message, StringComparison.Ordinal);
        if (targetExists)
        {
            Assert.Equal([1, 2, 3], File.ReadAllBytes(target));
        }
        else
        {
            Assert.False(File.Exists(target));
        }

        AssertNoTemporaryFiles();
        Assert.Equal(Path.Join(_root, "save"), Path.GetDirectoryName(written));
        Assert.Matches(TemporaryName(), Path.GetFileName(written));
    }

    [Theory]
    [InlineData("WriteAllBytes", "file.txt")]
    [InlineData("WriteAllBytes", "new/new.txt")]
    [InlineData("WriteAllBytes", "")]
    [InlineData("WriteAllBytes", "dir")]
    [InlineData("WriteAllBytes", "file.txt/child.txt")]
    [InlineData("Delete", "file.txt")]
    [InlineData("Delete", "empty")]
    [InlineData("Delete", "dir")]
    [InlineData("Delete", "missing.txt")]
    [InlineData("Delete", "")]
    [InlineData("CreateDirectory", "new/dir")]
    [InlineData("CreateDirectory", "dir")]
    [InlineData("CreateDirectory", "file.txt")]
    [InlineData("CreateDirectory", "")]
    public void ReadOnly_WritesUnsupported(string operation, string path)
    {
        // Several paths fail another way on a writable mount (ErrorRows), so a misordered check answers that instead.
        Seed();
        string before = Snapshot();
        DirectoryFileSystem fileSystem = Create(isReadOnly: true);

        Result result = Run(fileSystem, operation, path);

        Assert.True(fileSystem.IsReadOnly);
        AssertFailure(result, ErrorKind.Unsupported, 0, $"'{path}'");
        Assert.Equal(before, Snapshot());
        Assert.Equal("abc"u8.ToArray(), fileSystem.ReadAllBytes("file.txt").Value);
    }

    [Fact]
    public void Delete_NonEmptyDirectory_IsIo()
    {
        Seed();
        DirectoryFileSystem fileSystem = Create();

        Result result = fileSystem.Delete("dir");

        AssertFailure(result, ErrorKind.Io, null, "'dir'");
        Assert.True(File.Exists(Path.Join(_root, "dir", "inner.txt")));

        // Once it is empty, it can go.
        Assert.True(fileSystem.Delete("dir/inner.txt").IsSuccess);
        Assert.True(fileSystem.Delete("dir").IsSuccess);
        Assert.False(Directory.Exists(Path.Join(_root, "dir")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("assets")]
    [InlineData("./assets")]
    [InlineData("../assets")]
    [InlineData("assets/../other")]
    public void Constructor_RelativeRoot_Throws(string root)
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => new DirectoryFileSystem(new DirectoryFileSystemOptions { RootDirectory = root }));

        Assert.Equal("options", exception.ParamName);
    }

    [Fact]
    public void Constructor_NullOptionsOrRoot_Throws()
    {
        Assert.Equal("options", Assert.Throws<ArgumentNullException>(() => new DirectoryFileSystem(null!)).ParamName);
        Assert.Equal(
            "options",
            Assert.Throws<ArgumentNullException>(
                () => new DirectoryFileSystem(new DirectoryFileSystemOptions { RootDirectory = null! })).ParamName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Constructor_RootNotADirectory_Throws(bool isFile)
    {
        string root = Path.Join(_root, "root");
        if (isFile)
        {
            File.WriteAllBytes(root, [1]);
        }

        Assert.Throws<DirectoryNotFoundException>(() => new DirectoryFileSystem(new DirectoryFileSystemOptions { RootDirectory = root }));
    }

    [Fact]
    public void Constructor_NormalisesTheRoot()
    {
        string root = Path.Join(_root, "sub", "..") + Path.DirectorySeparatorChar;
        DirectoryFileSystemOptions options = new() { RootDirectory = root };

        DirectoryFileSystem fileSystem = new(options);

        Assert.False(options.IsReadOnly);
        Assert.True(fileSystem.WriteAllBytes("a.txt", [1]).IsSuccess);
        Assert.Equal([1], File.ReadAllBytes(Path.Join(_root, "a.txt")));
    }

    [Theory]
    [InlineData('a', 255)]
    [InlineData(Cjk, 85)]
    public void WriteAllBytes_MaxLengthName_Succeeds(char character, int count)
    {
        // 255 ASCII characters, or 85 ideographs (255 UTF-8 bytes): the longest name a VfsPath segment allows, and
        // the Linux limit. A temporary name built from the target's name would be longer than any OS allows.
        string name = new(character, count);
        string? temporary = null;
        DirectoryFileSystem fileSystem = new(Options(), stream => temporary = ((FileStream)stream).Name);

        Assert.True(fileSystem.WriteAllBytes(name, [4, 5]).IsSuccess);

        Assert.Equal([4, 5], File.ReadAllBytes(Path.Join(_root, name)));
        Assert.Equal([new VfsEntry(name, VfsEntryKind.File, 2)], fileSystem.List(string.Empty).Value);
        Assert.Matches(TemporaryName(), Path.GetFileName(temporary));
        AssertNoTemporaryFiles();
    }

    [Fact]
    public void WriteAllBytes_TargetOpenForRead_IsUnavailable()
    {
        File.WriteAllBytes(Path.Join(_root, "a.bin"), [1, 2, 3]);
        DirectoryFileSystem fileSystem = Create();
        using Stream reader = fileSystem.OpenRead("a.bin").Value;

        Result result = fileSystem.WriteAllBytes("a.bin", [7, 7]);

        if (OperatingSystem.IsWindows())
        {
            // Windows refuses to rename over a file that is open, and the file system doesn't retry.
            AssertFailure(result, ErrorKind.Unavailable, AccessDenied, "'a.bin'");
            Assert.Equal([1, 2, 3], File.ReadAllBytes(Path.Join(_root, "a.bin")));
        }
        else
        {
            // A POSIX rename replaces the name; the open reader keeps the old file.
            Assert.True(result.IsSuccess);
            Assert.Equal([7, 7], File.ReadAllBytes(Path.Join(_root, "a.bin")));
        }

        Assert.Equal([1, 2, 3], ReadToEnd(reader));
        AssertNoTemporaryFiles();
    }

    [Theory]
    [InlineData("file in the way", "ReadAllBytes", "file.txt/child", ErrorKind.NotFound, null)]
    [InlineData("file in the way", "OpenRead", "file.txt/child", ErrorKind.NotFound, null)]
    [InlineData("file in the way", "WriteAllBytes", "file.txt/child.txt", ErrorKind.NotFound, null)]
    [InlineData("file in the way", "WriteAllBytes", "file.txt/deeper/child.txt", ErrorKind.NotFound, null)]
    [InlineData("file in the way", "CreateDirectory", "file.txt", ErrorKind.NotFound, null)]
    [InlineData("file in the way", "CreateDirectory", "file.txt/child", ErrorKind.NotFound, null)]
    [InlineData("file in the way", "List", "file.txt", ErrorKind.NotFound, null)]
    [InlineData("file in the way", "List", "file.txt/child", ErrorKind.NotFound, null)]
    [InlineData("directory in the way", "ReadAllBytes", "dir", ErrorKind.NotFound, null)]
    [InlineData("directory in the way", "OpenRead", "dir", ErrorKind.NotFound, null)]
    [InlineData("directory in the way", "WriteAllBytes", "dir", ErrorKind.NotFound, null)]
    [InlineData("missing", "ReadAllBytes", "missing.txt", ErrorKind.NotFound, null)]
    [InlineData("missing", "ReadAllBytes", "missing/deeper.txt", ErrorKind.NotFound, null)]
    [InlineData("missing", "OpenRead", "missing.txt", ErrorKind.NotFound, null)]
    [InlineData("missing", "List", "missing", ErrorKind.NotFound, null)]
    [InlineData("access denied", "GetEntryKind", "dir/inner.txt", ErrorKind.Unavailable, AccessDenied)]
    [InlineData("access denied", "ReadAllBytes", "file.txt", ErrorKind.Unavailable, AccessDenied)]
    [InlineData("access denied", "OpenRead", "file.txt", ErrorKind.Unavailable, AccessDenied)]
    [InlineData("access denied", "WriteAllBytes", "dir/inner.txt", ErrorKind.Unavailable, AccessDenied)]
    [InlineData("access denied", "Delete", "dir/inner.txt", ErrorKind.Unavailable, AccessDenied)]
    [InlineData("access denied", "CreateDirectory", "dir/sub", ErrorKind.Unavailable, AccessDenied)]
    [InlineData("access denied", "List", "dir", ErrorKind.Unavailable, AccessDenied)]
    [InlineData("root removed", "ReadAllBytes", "a.txt", ErrorKind.NotFound, null)]
    [InlineData("root removed", "OpenRead", "a.txt", ErrorKind.NotFound, null)]
    [InlineData("root removed", "List", "", ErrorKind.NotFound, DirectoryNotFound)]
    [InlineData("root removed", "WriteAllBytes", "a.txt", ErrorKind.NotFound, DirectoryNotFound)]
    [InlineData("root removed", "WriteAllBytes", "dir/a.txt", ErrorKind.NotFound, DirectoryNotFound)]
    [InlineData("root removed", "CreateDirectory", "dir", ErrorKind.NotFound, 0)]
    [InlineData("root removed", "CreateDirectory", "", ErrorKind.NotFound, 0)]
    [InlineData("root removed", "Delete", "a.txt", ErrorKind.NotFound, 0)]
    public void OsFailures_MapWithNativeCode(string scenario, string operation, string path, ErrorKind expected, int? nativeCode)
    {
        // Real OS failures, as each OS reports them; a null code is any non-zero one, because the OSes differ. Access
        // is denied with Windows' read-only attribute (which stops replacing and deleting a file only) or POSIX
        // permissions (the CI runners and the dev host don't run tests as root, which ignores them). A removed root
        // fails with the failed call's code where an OS call failed, and with 0 where the check is made first, so
        // that nothing creates the root again.
        Seed();
        string before = Snapshot();
        DirectoryFileSystem fileSystem = Create();
        bool deniedOnWindows = scenario == "access denied" && OperatingSystem.IsWindows();
        if (scenario == "root removed")
        {
            Directory.Delete(_root, recursive: true);
        }

        Result result;
        try
        {
            if (scenario == "access denied")
            {
                SetAccess(operation, allowed: false);
            }

            result = Run(fileSystem, operation, path);
        }
        finally
        {
            if (scenario == "access denied")
            {
                SetAccess(operation, allowed: true);
            }
        }

        if (deniedOnWindows && operation is not ("WriteAllBytes" or "Delete"))
        {
            Assert.True(result.IsSuccess);
            return;
        }

        AssertFailure(result, expected, nativeCode, $"'{path}'");
        if (scenario == "root removed")
        {
            Assert.Equal(VfsEntryKind.None, fileSystem.GetEntryKind(string.Empty).Value);
            Assert.False(Directory.Exists(_root));
        }
        else
        {
            Assert.Equal(before, Snapshot());
            AssertNoTemporaryFiles();
        }
    }

    [Theory]
    [InlineData("ReadAllBytes", "", ErrorKind.NotFound)]
    [InlineData("OpenRead", "", ErrorKind.NotFound)]
    [InlineData("WriteAllBytes", "", ErrorKind.NotFound)]
    [InlineData("Delete", "", ErrorKind.Unsupported)]
    [InlineData("Delete", "missing.txt", ErrorKind.NotFound)]
    [InlineData("Delete", "file.txt/child", ErrorKind.NotFound)]
    public void ErrorRows_WithoutAnOsFailure_HaveNativeCodeZero(string operation, string path, ErrorKind expected)
    {
        Seed();
        string before = Snapshot();
        DirectoryFileSystem fileSystem = Create();

        (bool failed, EngineError error) = Outcome(Run(fileSystem, operation, path));

        Assert.True(failed, "The operation succeeded.");
        Assert.Equal(expected, error.Kind);
        Assert.Equal(0, error.NativeCode);
        Assert.Contains($"'{path}'", error.Message, StringComparison.Ordinal);
        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public void ReadAllBytes_FileOverArrayMaxLength_IsUnsupported()
    {
        // A sparse file on Linux; NTFS reserves the space without writing it. Nothing reads its contents.
        using (FileStream big = new(Path.Join(_root, "big.bin"), FileMode.CreateNew))
        {
            big.SetLength(Array.MaxLength + 1L);
        }

        DirectoryFileSystem fileSystem = Create();

        AssertFailure(fileSystem.ReadAllBytes("big.bin").ToResult(), ErrorKind.Unsupported, 0, "'big.bin'");
        using Stream stream = fileSystem.OpenRead("big.bin").Value;
        Assert.Equal(Array.MaxLength + 1L, stream.Length);
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
        // Read-only, so a misordered check that answers Unsupported before looking at the argument fails too.
        DirectoryFileSystem fileSystem = Create(isReadOnly: true);

        Assert.Equal("relativePath", Assert.Throws<ArgumentNullException>(() => Run(fileSystem, operation, null!)).ParamName);
    }

    public static TheoryData<string, string> NotNormalisedPaths()
    {
        TheoryData<string, string> rows = [];
        string[] paths = ["/a", "a/", "a//b", ".", "a/./b", "..", "a/../b", "a\\b", "a:b", "a.", "CON", "\uD800", new string('a', 256)];
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
        Seed();
        string before = Snapshot();
        DirectoryFileSystem fileSystem = Create(isReadOnly: true);

        Assert.Equal("relativePath", Assert.Throws<ArgumentException>(() => Run(fileSystem, operation, path)).ParamName);
        Assert.Equal(before, Snapshot());
    }

    [GeneratedRegex(@"^\.[0-9a-f]{16}\.betmp$")]
    private static partial Regex TemporaryName();

    private static Result Run(DirectoryFileSystem fileSystem, string operation, string path) => operation switch
    {
        "GetEntryKind" => fileSystem.GetEntryKind(path).ToResult(),
        "ReadAllBytes" => fileSystem.ReadAllBytes(path).ToResult(),
        "OpenRead" => Dispose(fileSystem.OpenRead(path)),
        "WriteAllBytes" => fileSystem.WriteAllBytes(path, [9, 9]),
        "Delete" => fileSystem.Delete(path),
        "CreateDirectory" => fileSystem.CreateDirectory(path),
        "List" => fileSystem.List(path).ToResult(),
        _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unknown operation."),
    };

    private static Result Dispose(Result<Stream> opened)
    {
        if (opened.TryGetValue(out Stream? stream))
        {
            stream.Dispose();
        }

        return opened.ToResult();
    }

    private static (bool Failed, EngineError Error) Outcome(Result result) =>
        (result.IsFailure, result.IsFailure ? result.Error : default);

    /// <summary>Asserts a failure's kind, its native code (null: any non-zero code) and a quoted path.</summary>
    private static void AssertFailure(Result result, ErrorKind kind, int? nativeCode, string quoted)
    {
        Assert.True(result.IsFailure, "The operation succeeded.");
        Assert.Equal(kind, result.Error.Kind);
        if (nativeCode is { } code)
        {
            Assert.Equal(code, result.Error.NativeCode);
        }
        else
        {
            Assert.NotEqual(0, result.Error.NativeCode);
        }

        Assert.Contains(quoted, result.Error.Message, StringComparison.Ordinal);
    }

    private static byte[] ReadToEnd(Stream stream)
    {
        using MemoryStream copy = new();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    private DirectoryFileSystemOptions Options(bool isReadOnly = false) => new() { RootDirectory = _root, IsReadOnly = isReadOnly };

    private DirectoryFileSystem Create(bool isReadOnly = false) => new(Options(isReadOnly));

    /// <summary>Seeds the root through System.IO: file.txt ("abc"), dir/inner.txt ([1]) and an empty directory.</summary>
    private void Seed()
    {
        File.WriteAllBytes(Path.Join(_root, "file.txt"), "abc"u8.ToArray());
        Directory.CreateDirectory(Path.Join(_root, "dir"));
        File.WriteAllBytes(Path.Join(_root, "dir", "inner.txt"), [1]);
        Directory.CreateDirectory(Path.Join(_root, "empty"));
    }

    /// <summary>Denies, or allows again, the access an "access denied" row needs, on the seeded tree.</summary>
    private void SetAccess(string operation, bool allowed)
    {
        string inner = Path.Join(_root, "dir", "inner.txt");
        if (OperatingSystem.IsWindows())
        {
            File.SetAttributes(inner, allowed ? FileAttributes.Normal : FileAttributes.ReadOnly);
            return;
        }

        const UnixFileMode all = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        File.SetUnixFileMode(Path.Join(_root, "file.txt"), allowed ? UnixFileMode.UserRead | UnixFileMode.UserWrite : UnixFileMode.None);
        File.SetUnixFileMode(Path.Join(_root, "dir"), allowed ? all : operation switch
        {
            "GetEntryKind" => UnixFileMode.UserRead | UnixFileMode.UserWrite,
            "List" => UnixFileMode.UserExecute,
            _ => UnixFileMode.UserRead | UnixFileMode.UserExecute,
        });
    }

    /// <summary>Every entry under the root with its contents, read through System.IO, in ordinal order.</summary>
    private string Snapshot()
    {
        StringBuilder text = new();
        foreach (string path in Directory.EnumerateFileSystemEntries(_root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            text.Append(Path.GetRelativePath(_root, path))
                .Append(Directory.Exists(path) ? "/" : "=" + Convert.ToHexString(File.ReadAllBytes(path)))
                .Append('\n');
        }

        return text.ToString();
    }

    private void AssertNoTemporaryFiles() =>
        Assert.Empty(Directory.EnumerateFiles(_root, "*.betmp", SearchOption.AllDirectories));
}
