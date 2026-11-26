using System;
using System.Collections.Generic;
using System.IO;
using BrutalEngine.Core;
using BrutalEngine.Platform.Vfs;
using Xunit;

namespace BrutalEngine.Platform.Tests.Vfs;

public sealed class VirtualFileSystemTests
{
    private static readonly string[] _operations =
        ["Exists", "GetEntryKind", "ReadAllBytes", "OpenRead", "WriteAllBytes", "Delete", "CreateDirectory", "List"];

    public static TheoryData<string> EveryOperation() => [.. _operations];

    [Theory]
    [MemberData(nameof(EveryOperation))]
    public void Read_RoutesToMountWithRelativePath(string operation)
    {
        // Mounts whose schemes share prefixes with the path's, so a prefix or substring match routes wrongly.
        VirtualFileSystem vfs = new();
        RecordingFileSystem res = new();
        RecordingFileSystem re = new();
        RecordingFileSystem res2 = new();
        RecordingFileSystem user = new();
        vfs.Mount("re", re);
        vfs.Mount("res", res);
        vfs.Mount("res2", res2);
        vfs.Mount("user", user);

        object result = Run(vfs, operation, VfsPath.Parse("res://textures//ui/./button.png/"));

        Assert.Equal([$"{Underlying(operation)} textures/ui/button.png"], res.Calls);
        Assert.Empty(re.Calls);
        Assert.Empty(res2.Calls);
        Assert.Empty(user.Calls);
        AssertIsResultOf(res, operation, result);
        if (operation == "WriteAllBytes")
        {
            Assert.Equal([5, 6, 7], res.LastContents);
        }
    }

    [Theory]
    [MemberData(nameof(EveryOperation))]
    public void Root_RoutesWithEmptyRelativePath(string operation)
    {
        VirtualFileSystem vfs = new();
        RecordingFileSystem user = new();
        vfs.Mount("user", user);

        _ = Run(vfs, operation, VfsPath.Parse("user://"));

        Assert.Equal([$"{Underlying(operation)} "], user.Calls);
    }

    [Theory]
    [MemberData(nameof(EveryOperation))]
    public void UnmountedScheme_IsNotFound(string operation)
    {
        VirtualFileSystem vfs = new();
        RecordingFileSystem res = new();
        vfs.Mount("res", res);
        vfs.Mount("user", new RecordingFileSystem());
        Assert.True(vfs.Unmount("user"));

        foreach (string text in new[] { "user://saves/slot1.sav", "re://a", "res2://a", "test-assets://" })
        {
            (bool failed, EngineError error) = Outcome(Run(vfs, operation, VfsPath.Parse(text)));

            Assert.True(failed, text);
            Assert.Equal(ErrorKind.NotFound, error.Kind);
            Assert.Equal(0, error.NativeCode);
            Assert.Contains(VfsPath.Parse(text).Scheme + "://", error.Message, StringComparison.Ordinal);
        }

        Assert.Empty(res.Calls);
    }

    [Fact]
    public void Mount_Twice_Throws()
    {
        VirtualFileSystem vfs = new();
        RecordingFileSystem first = new();
        RecordingFileSystem second = new();
        vfs.Mount("res", first);

        InvalidOperationException again = Assert.Throws<InvalidOperationException>(() => vfs.Mount("res", second));
        Assert.Throws<InvalidOperationException>(() => vfs.Mount("res", first));

        Assert.Contains("'res://'", again.Message, StringComparison.Ordinal);
        Assert.True(vfs.GetEntryKind(VfsPath.Parse("res://a")).IsSuccess);
        Assert.Single(first.Calls);
        Assert.Empty(second.Calls);
    }

    [Theory]
    [MemberData(nameof(EveryOperation))]
    public void DefaultPath_Throws(string operation)
    {
        VirtualFileSystem vfs = new();
        RecordingFileSystem res = new();
        vfs.Mount("res", res);

        ArgumentException exception = Assert.Throws<ArgumentException>(() => Run(vfs, operation, default));

        Assert.Equal(operation == "List" ? "directory" : "path", exception.ParamName);
        Assert.Empty(res.Calls);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Res")]
    [InlineData("RES")]
    [InlineData("1res")]
    [InlineData("-res")]
    [InlineData("res:")]
    [InlineData("res://")]
    [InlineData("res:/")]
    [InlineData("res/")]
    [InlineData("res/a")]
    [InlineData("res://a")]
    [InlineData("re s")]
    [InlineData("res_1")]
    [InlineData("res.x")]
    [InlineData("r\u00E9s")]
    [InlineData("res\u0000")]
    [InlineData(" res")]
    [InlineData("abcdefghijklmnopqrstuvwxyz0123456")]
    public void Mount_InvalidScheme_Throws(string scheme)
    {
        VirtualFileSystem vfs = new();

        ArgumentException exception = Assert.Throws<ArgumentException>(() => vfs.Mount(scheme, new RecordingFileSystem()));

        Assert.Equal("scheme", exception.ParamName);
        Assert.False(vfs.IsMounted(scheme));
        Assert.False(vfs.Unmount(scheme));
    }

    [Theory]
    [InlineData("r")]
    [InlineData("res")]
    [InlineData("a1-")]
    [InlineData("a-")]
    [InlineData("test-assets")]
    [InlineData("z9")]
    [InlineData("abcdefghijklmnopqrstuvwxyz012345")]
    [InlineData("abcdefghijklmnopqrstuvwxyz01234")]
    public void Mount_ValidLookAlikeScheme_IsAccepted(string scheme)
    {
        VirtualFileSystem vfs = new();
        RecordingFileSystem fileSystem = new();

        vfs.Mount(scheme, fileSystem);

        Assert.True(vfs.IsMounted(scheme));
        Assert.True(vfs.Exists(VfsPath.Parse(scheme + "://a")).IsSuccess);
        Assert.Equal(["GetEntryKind a"], fileSystem.Calls);
    }

    [Fact]
    public void NullArguments_Throw()
    {
        VirtualFileSystem vfs = new();

        Assert.Equal("scheme", Assert.Throws<ArgumentNullException>(() => vfs.Mount(null!, new RecordingFileSystem())).ParamName);
        Assert.Equal("fileSystem", Assert.Throws<ArgumentNullException>(() => vfs.Mount("res", null!)).ParamName);
        Assert.Equal("scheme", Assert.Throws<ArgumentNullException>(() => vfs.Unmount(null!)).ParamName);
        Assert.Equal("scheme", Assert.Throws<ArgumentNullException>(() => vfs.IsMounted(null!)).ParamName);
        Assert.False(vfs.IsMounted("res"));
    }

    [Fact]
    public void Unmount_RemovesOnlyThatMount_AndAllowsMountingAgain()
    {
        VirtualFileSystem vfs = new();
        RecordingFileSystem a = new();
        RecordingFileSystem b = new();
        RecordingFileSystem c = new();
        vfs.Mount("a", a);
        vfs.Mount("b", b);
        vfs.Mount("c", c);

        Assert.True(vfs.Unmount("b"));
        Assert.False(vfs.Unmount("b"));

        Assert.True(vfs.IsMounted("a"));
        Assert.False(vfs.IsMounted("b"));
        Assert.True(vfs.IsMounted("c"));
        Assert.True(vfs.Exists(VfsPath.Parse("a://x")).IsSuccess);
        Assert.True(vfs.Exists(VfsPath.Parse("c://x")).IsSuccess);
        Assert.Equal(ErrorKind.NotFound, vfs.Exists(VfsPath.Parse("b://x")).Error.Kind);

        RecordingFileSystem replacement = new();
        vfs.Mount("b", replacement);
        Assert.True(vfs.Exists(VfsPath.Parse("b://x")).IsSuccess);
        Assert.Single(replacement.Calls);
        Assert.Empty(b.Calls);
    }

    [Fact]
    public void Mounts_AreNotDisposed()
    {
        Assert.False(typeof(IDisposable).IsAssignableFrom(typeof(VirtualFileSystem)));
        Assert.False(typeof(IDisposable).IsAssignableFrom(typeof(IFileSystem)));

        VirtualFileSystem vfs = new();
        DisposableFileSystem fileSystem = new();
        vfs.Mount("res", fileSystem);
        Assert.True(vfs.ReadAllBytes(VfsPath.Parse("res://a")).IsSuccess);
        Assert.True(vfs.Unmount("res"));
        vfs.Mount("res", fileSystem);
        Assert.True(vfs.Unmount("res"));

        Assert.Equal(0, fileSystem.DisposeCount);
        Assert.Equal(["ReadAllBytes a"], fileSystem.Calls);
    }

    [Theory]
    [InlineData(ErrorKind.NotFound, 0)]
    [InlineData(ErrorKind.Unsupported, 0)]
    [InlineData(ErrorKind.Unavailable, 5)]
    [InlineData(ErrorKind.Io, -2147024784)]
    public void Failures_PassThroughUnchanged(ErrorKind kind, int nativeCode)
    {
        VirtualFileSystem vfs = new();
        EngineError failure = new(kind, "the file system's own message", nativeCode);
        RecordingFileSystem res = new() { Failure = failure };
        vfs.Mount("res", res);

        foreach (string operation in _operations)
        {
            (bool failed, EngineError error) = Outcome(Run(vfs, operation, VfsPath.Parse("res://a/b")));

            Assert.True(failed, operation);
            Assert.Equal(failure, error);
        }
    }

    [Theory]
    [InlineData(VfsEntryKind.File, true)]
    [InlineData(VfsEntryKind.Directory, true)]
    [InlineData(VfsEntryKind.None, false)]
    public void Exists_IsTrueForFilesAndDirectories(VfsEntryKind kind, bool expected)
    {
        VirtualFileSystem vfs = new();
        vfs.Mount("res", new RecordingFileSystem { Kind = kind });

        Assert.Equal(expected, vfs.Exists(VfsPath.Parse("res://a")).Value);
    }

    [Fact]
    public void FileSystemExceptions_ReachTheCaller()
    {
        VirtualFileSystem vfs = new();
        vfs.Mount("res", new RecordingFileSystem { Throw = true });

        foreach (string operation in _operations)
        {
            Assert.Throws<IOException>(() => Run(vfs, operation, VfsPath.Parse("res://a")));
        }
    }

    [Fact]
    public void InMemoryMount_RoundTripsThroughThePath()
    {
        VirtualFileSystem vfs = new();
        InMemoryFileSystem memory = new();
        vfs.Mount("test-assets", memory);

        Assert.True(vfs.WriteAllBytes(VfsPath.Parse("test-assets://a//b/./c.bin"), [1, 2]).IsSuccess);

        Assert.Equal([1, 2], memory.ReadAllBytes("a/b/c.bin").Value);
        Assert.Equal([1, 2], vfs.ReadAllBytes(VfsPath.Parse("test-assets://a/b/c.bin")).Value);
        Assert.Equal([new VfsEntry("b", VfsEntryKind.Directory, 0)], vfs.List(VfsPath.Parse("test-assets://a")).Value);
        Assert.True(vfs.Exists(VfsPath.Parse("test-assets://a/b")).Value);
        Assert.False(vfs.Exists(VfsPath.Parse("test-assets://a/z")).Value);
    }

    private static string Underlying(string operation) => operation == "Exists" ? "GetEntryKind" : operation;

    private static object Run(VirtualFileSystem vfs, string operation, VfsPath path) => operation switch
    {
        "Exists" => vfs.Exists(path),
        "GetEntryKind" => vfs.GetEntryKind(path),
        "ReadAllBytes" => vfs.ReadAllBytes(path),
        "OpenRead" => vfs.OpenRead(path),
        "WriteAllBytes" => vfs.WriteAllBytes(path, [5, 6, 7]),
        "Delete" => vfs.Delete(path),
        "CreateDirectory" => vfs.CreateDirectory(path),
        "List" => vfs.List(path),
        _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unknown operation."),
    };

    private static void AssertIsResultOf(RecordingFileSystem fileSystem, string operation, object result)
    {
        // The file system's own result objects come back: the same array, stream and list instances.
        switch (operation)
        {
            case "Exists":
                Assert.True(((Result<bool>)result).Value);
                break;
            case "GetEntryKind":
                Assert.Equal(VfsEntryKind.File, ((Result<VfsEntryKind>)result).Value);
                break;
            case "ReadAllBytes":
                Assert.Same(fileSystem.Bytes, ((Result<byte[]>)result).Value);
                break;
            case "OpenRead":
                Assert.Same(fileSystem.Stream, ((Result<Stream>)result).Value);
                break;
            case "List":
                Assert.Same(fileSystem.Entries, ((Result<IReadOnlyList<VfsEntry>>)result).Value);
                break;
            default:
                Assert.True(((Result)result).IsSuccess);
                break;
        }
    }

    private static (bool Failed, EngineError Error) Outcome(object result) => result switch
    {
        Result r => (r.IsFailure, r.IsFailure ? r.Error : default),
        Result<bool> r => (r.IsFailure, r.IsFailure ? r.Error : default),
        Result<VfsEntryKind> r => (r.IsFailure, r.IsFailure ? r.Error : default),
        Result<byte[]> r => (r.IsFailure, r.IsFailure ? r.Error : default),
        Result<Stream> r => (r.IsFailure, r.IsFailure ? r.Error : default),
        Result<IReadOnlyList<VfsEntry>> r => (r.IsFailure, r.IsFailure ? r.Error : default),
        _ => throw new ArgumentOutOfRangeException(nameof(result), result, "Not a result."),
    };

    /// <summary>Records each call as "Operation relativePath" and answers with fixed instances, or a failure.</summary>
    private class RecordingFileSystem : IFileSystem
    {
        public List<string> Calls { get; } = [];

        public byte[] Bytes { get; } = [1, 2, 3];

        public MemoryStream Stream { get; } = new([1, 2, 3], writable: false);

        public VfsEntry[] Entries { get; } = [new("a", VfsEntryKind.File, 1)];

        public byte[]? LastContents { get; private set; }

        public EngineError? Failure { get; init; }

        public VfsEntryKind Kind { get; init; } = VfsEntryKind.File;

        public bool Throw { get; init; }

        public bool IsReadOnly => false;

        public Result<VfsEntryKind> GetEntryKind(string relativePath) =>
            Record(nameof(GetEntryKind), relativePath) ?? new Result<VfsEntryKind>(Kind);

        public Result<byte[]> ReadAllBytes(string relativePath) => Record(nameof(ReadAllBytes), relativePath) ?? new Result<byte[]>(Bytes);

        public Result<Stream> OpenRead(string relativePath) => Record(nameof(OpenRead), relativePath) ?? new Result<Stream>(Stream);

        public Result WriteAllBytes(string relativePath, ReadOnlySpan<byte> contents)
        {
            LastContents = contents.ToArray();
            return Record(nameof(WriteAllBytes), relativePath) is { } failure ? Result.Failure(failure) : Result.Success;
        }

        public Result Delete(string relativePath) =>
            Record(nameof(Delete), relativePath) is { } failure ? Result.Failure(failure) : Result.Success;

        public Result CreateDirectory(string relativePath) =>
            Record(nameof(CreateDirectory), relativePath) is { } failure ? Result.Failure(failure) : Result.Success;

        public Result<IReadOnlyList<VfsEntry>> List(string relativePath) =>
            Record(nameof(List), relativePath) ?? new Result<IReadOnlyList<VfsEntry>>(Entries);

        private EngineError? Record(string operation, string relativePath)
        {
            Calls.Add($"{operation} {relativePath}");
            return Throw ? throw new IOException("thrown by the file system") : Failure;
        }
    }

    private sealed class DisposableFileSystem : RecordingFileSystem, IDisposable
    {
        public int DisposeCount { get; private set; }

        public void Dispose() => DisposeCount++;
    }
}
