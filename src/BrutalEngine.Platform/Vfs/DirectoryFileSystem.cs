using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using BrutalEngine.Core;

namespace BrutalEngine.Platform.Vfs;

/// <summary>
/// A file system over a real directory, for the <c>res://</c> and <c>user://</c> mounts: every relative path is joined
/// under <see cref="DirectoryFileSystemOptions.RootDirectory"/>, and <see cref="WriteAllBytes"/> replaces files
/// atomically.
/// </summary>
/// <remarks>
/// <para>
/// <b>Paths:</b> a relative path must be a normalised <see cref="VfsPath.RelativePath"/>, so it can't leave the root
/// lexically; its <c>/</c> separators become the OS's, and the OS resolves the result. Lookups follow the OS too: on a
/// case-insensitive file system one spelling opens another. If the root directory is removed after construction,
/// <see cref="GetEntryKind"/> answers <see cref="VfsEntryKind.None"/> for every path, the root included, every other
/// operation fails with <see cref="ErrorKind.NotFound"/>, and none creates the root again unless the root is removed
/// during the call, in the window between checking that it exists and creating a directory, which the OS gives no way
/// to close.
/// </para>
/// <para>
/// <b>Symbolic links</b> are followed wherever they lead, except at the last name of a write or of a file's delete: the
/// rename of <see cref="WriteAllBytes"/> replaces a link at the target name with a regular file, and
/// <see cref="Delete"/> of a link to a file removes the link; both leave the link's target untouched.
/// <see cref="List"/> lists a link as what it leads to, through any chain of links, with the final target's length.
/// A link that leads to no file (dangling, or a loop) is a <see cref="VfsEntryKind.File"/> to
/// <see cref="GetEntryKind"/> and a file of length 0 to <see cref="List"/>; <see cref="ReadAllBytes"/> of a dangling
/// link fails with <see cref="ErrorKind.NotFound"/>, and <see cref="Delete"/> removes the link.
/// </para>
/// <para>
/// <b>Writes:</b> <see cref="WriteAllBytes"/> creates missing parent directories, writes the bytes to a new temporary
/// file in the target's directory, flushes it to disk, then renames it over the target, so a reader or a crash sees the
/// old contents or the new ones, never a part. The temporary name is <c>.</c>, 16 random lowercase hexadecimal digits
/// and <c>.betmp</c>, whatever the target's name, so a 255-byte name still has room. If the write fails, the temporary
/// file is deleted and the target keeps its old contents; parent directories created on the way stay.
/// <see cref="List"/> doesn't hide temporary files (of a write in progress, or left by a crash): hiding the pattern
/// would hide a real file too. On Windows, a file that a stream has open (an <see cref="OpenRead"/> stream included)
/// can't be replaced: the write fails with <see cref="ErrorKind.Unavailable"/>, and isn't retried.
/// </para>
/// <para>
/// <b>Failures</b> follow <see cref="IFileSystem"/>. On a read-only file system, writes, deletes and creates fail with
/// <see cref="ErrorKind.Unsupported"/> before the disk is looked at, and so does deleting the root;
/// <see cref="ReadAllBytes"/> of a file over <see cref="Array.MaxLength"/> bytes is <see cref="ErrorKind.Unsupported"/>
/// too, found once the file is open. When an OS call fails, the error has the OS message and the exception's
/// <see cref="Exception.HResult"/> as <see cref="EngineError.NativeCode"/>: a missing entry, a file where a directory
/// is expected or the reverse, and a removed root are <see cref="ErrorKind.NotFound"/>; access denied is
/// <see cref="ErrorKind.Unavailable"/>; anything else, such as deleting a non-empty directory, is
/// <see cref="ErrorKind.Io"/>. A failure found without a failing OS call has <see cref="EngineError.NativeCode"/> 0:
/// the root read or written as a file, deleting a path where nothing is, and creating a directory under a removed
/// root. Messages quote the relative path. A <see langword="null"/> path throws <see cref="ArgumentNullException"/>,
/// and text that isn't a normalised relative path throws <see cref="ArgumentException"/>, before anything else.
/// </para>
/// <para>
/// <b>Threads:</b> every member is safe on any thread, as far as the OS is: the file system holds no mutable state and
/// each call goes to the OS. Concurrent writers of one path race, and the last rename wins.
/// </para>
/// <para>
/// <b>Ownership and allocation:</b> the file system holds nothing of the OS's between calls and isn't disposable;
/// streams from <see cref="OpenRead"/> belong to the caller. Every operation allocates (paths, OS objects, results,
/// arrays, lists, messages); <see cref="IsReadOnly"/> allocates nothing. File I/O is not a hot path. See
/// docs/design/platform.md section 5, "Mounts and operations".
/// </para>
/// </remarks>
public sealed class DirectoryFileSystem : IFileSystem
{
    private readonly string _root;
    private readonly bool _isReadOnly;
    private readonly Action<Stream>? _afterTemporaryWrite;
    private readonly Action<FileInfo>? _beforeListedFileMeasured;

    /// <summary>Creates a file system over an existing directory.</summary>
    /// <remarks>Reads the options once. Allocates the normalised root path.</remarks>
    /// <param name="options">The root directory, and whether the file system is read-only.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="options"/> or its <see cref="DirectoryFileSystemOptions.RootDirectory"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <see cref="DirectoryFileSystemOptions.RootDirectory"/> isn't a fully qualified path (such as an empty or a
    /// relative one), or the OS rejects it.
    /// </exception>
    /// <exception cref="DirectoryNotFoundException">No directory exists at the root (nothing, or a file).</exception>
    public DirectoryFileSystem(DirectoryFileSystemOptions options)
        : this(options, null, null)
    {
    }

    /// <summary>
    /// Creates a file system with hooks for tests, which can make a write fail half-way or change an entry while it is
    /// listed.
    /// </summary>
    /// <param name="options">As for the public constructor.</param>
    /// <param name="afterTemporaryWrite">
    /// Called with the temporary file's stream after the bytes are written and before the flush, or
    /// <see langword="null"/>; an <see cref="IOException"/> it throws fails the write.
    /// </param>
    /// <param name="beforeListedFileMeasured">
    /// Called by <see cref="List"/> with each file entry the OS listed, before its length is taken, or
    /// <see langword="null"/>.
    /// </param>
    internal DirectoryFileSystem(
        DirectoryFileSystemOptions options,
        Action<Stream>? afterTemporaryWrite,
        Action<FileInfo>? beforeListedFileMeasured)
    {
        ArgumentNullException.ThrowIfNull(options);
        string root = options.RootDirectory ?? throw new ArgumentNullException(nameof(options), "RootDirectory is null.");
        if (!Path.IsPathFullyQualified(root))
        {
            throw new ArgumentException($"RootDirectory must be a fully qualified path, and '{root}' isn't.", nameof(options));
        }

        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!Directory.Exists(_root))
        {
            throw new DirectoryNotFoundException($"RootDirectory '{root}' is not an existing directory.");
        }

        _isReadOnly = options.IsReadOnly;
        _afterTemporaryWrite = afterTemporaryWrite;
        _beforeListedFileMeasured = beforeListedFileMeasured;
    }

    /// <summary>Whether the file system refuses every write, delete and create.</summary>
    /// <value>
    /// <see cref="DirectoryFileSystemOptions.IsReadOnly"/> as given to the constructor; it never changes. Any thread.
    /// Allocates nothing.
    /// </value>
    public bool IsReadOnly => _isReadOnly;

    /// <inheritdoc/>
    public Result<VfsEntryKind> GetEntryKind(string relativePath)
    {
        ThrowIfNotNormalised(relativePath);
        try
        {
            return KindOf(GetFullPath(relativePath));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Failure(exception, relativePath);
        }
    }

    /// <inheritdoc/>
    public Result<byte[]> ReadAllBytes(string relativePath)
    {
        ThrowIfNotNormalised(relativePath);
        if (relativePath.Length == 0)
        {
            return RootIsNotAFile();
        }

        string fullPath = GetFullPath(relativePath);
        try
        {
            using FileStream stream = new(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 0);
            long length = stream.Length;
            if (length > Array.MaxLength)
            {
                return new EngineError(
                    ErrorKind.Unsupported,
                    string.Create(CultureInfo.InvariantCulture, $"'{relativePath}' has {length} bytes, more than an array holds ({Array.MaxLength})."));
            }

            byte[] contents = new byte[length];
            stream.ReadExactly(contents);
            return contents;
        }
        catch (UnauthorizedAccessException exception) when (Directory.Exists(fullPath))
        {
            return IsADirectory(exception, relativePath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Failure(exception, relativePath);
        }
    }

    /// <inheritdoc/>
    /// <remarks>The stream is a <see cref="FileStream"/> that shares the file with readers only.</remarks>
    public Result<Stream> OpenRead(string relativePath)
    {
        ThrowIfNotNormalised(relativePath);
        if (relativePath.Length == 0)
        {
            return RootIsNotAFile();
        }

        string fullPath = GetFullPath(relativePath);
        try
        {
            return new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (UnauthorizedAccessException exception) when (Directory.Exists(fullPath))
        {
            return IsADirectory(exception, relativePath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Failure(exception, relativePath);
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Writes a temporary sibling, flushes it to disk and renames it over the target; on failure the temporary file is
    /// deleted and the target keeps its old contents. The class remarks give the temporary name and the Windows rule
    /// for open files.
    /// </remarks>
    public Result WriteAllBytes(string relativePath, ReadOnlySpan<byte> contents)
    {
        ThrowIfNotNormalised(relativePath);
        if (_isReadOnly)
        {
            return ReadOnly(relativePath);
        }

        if (relativePath.Length == 0)
        {
            return RootIsNotAFile();
        }

        string fullPath = GetFullPath(relativePath);
        string directory = Path.GetDirectoryName(fullPath)!;
        string temporaryPath = Path.Join(directory, string.Concat(".", RandomNumberGenerator.GetHexString(16, lowercase: true), ".betmp"));
        Result<FileStream> created = CreateTemporary(relativePath, directory, temporaryPath);
        if (!created.TryGetValue(out FileStream? stream))
        {
            return created.Error;
        }

        try
        {
            using (stream)
            {
                stream.Write(contents);
                _afterTemporaryWrite?.Invoke(stream);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, fullPath, overwrite: true);
            return Result.Success;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            DeleteTemporary(temporaryPath);
            return Directory.Exists(fullPath) ? IsADirectory(exception, relativePath) : Failure(exception, relativePath);
        }
    }

    /// <inheritdoc/>
    public Result Delete(string relativePath)
    {
        ThrowIfNotNormalised(relativePath);
        if (_isReadOnly)
        {
            return ReadOnly(relativePath);
        }

        if (relativePath.Length == 0)
        {
            return new EngineError(ErrorKind.Unsupported, "'' is the root directory, which can't be deleted.");
        }

        string fullPath = GetFullPath(relativePath);
        try
        {
            switch (KindOf(fullPath))
            {
                case VfsEntryKind.None:
                    return new EngineError(ErrorKind.NotFound, $"Nothing exists at '{relativePath}'.");
                case VfsEntryKind.Directory:
                    Directory.Delete(fullPath, recursive: false);
                    break;
                default:
                    File.Delete(fullPath);
                    break;
            }

            return Result.Success;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Failure(exception, relativePath);
        }
    }

    /// <inheritdoc/>
    public Result CreateDirectory(string relativePath)
    {
        ThrowIfNotNormalised(relativePath);
        if (_isReadOnly)
        {
            return ReadOnly(relativePath);
        }

        try
        {
            // Directory.CreateDirectory creates every missing ancestor, so it would bring a removed root back. A root
            // removed after this check and before the call still comes back: the OS gives no way to close that window.
            if (!Directory.Exists(_root))
            {
                return RootIsGone(relativePath, 0);
            }

            Directory.CreateDirectory(GetFullPath(relativePath));
            return Result.Success;
        }
        catch (IOException exception) when (FindFile(relativePath, includeSelf: true) is { } file)
        {
            return FileInTheWay(file, relativePath, exception.HResult);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Failure(exception, relativePath);
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Lists every entry the OS reports, including names a <see cref="VfsPath"/> can't address and temporary files. A
    /// symbolic link is listed as what it leads to, through any chain of links: a directory, or a file with the final
    /// target's length; a link that leads to no file (dangling, or a loop) is a file of length 0. Each entry is measured
    /// after the OS lists it: a link removed in between is left out, because resolving it finds nothing, but a regular
    /// file removed in between is still listed, with the length the listing read, and so is an entry that is no longer
    /// a link when it is resolved.
    /// </remarks>
    public Result<IReadOnlyList<VfsEntry>> List(string relativePath)
    {
        ThrowIfNotNormalised(relativePath);
        string fullPath = GetFullPath(relativePath);
        try
        {
            List<VfsEntry> entries = [];
            foreach (FileSystemInfo info in new DirectoryInfo(fullPath).EnumerateFileSystemInfos())
            {
                if (info is not FileInfo file)
                {
                    entries.Add(new VfsEntry(info.Name, VfsEntryKind.Directory, 0));
                    continue;
                }

                _beforeListedFileMeasured?.Invoke(file);
                try
                {
                    entries.Add(new VfsEntry(file.Name, VfsEntryKind.File, ListedLength(file)));
                }
                catch (FileNotFoundException)
                {
                    // Only resolving a link reaches the OS here (a regular file's length is the listing's): the link
                    // was removed after it was listed and before it was measured, so it is gone and isn't listed.
                }
            }

            entries.Sort(static (left, right) => string.CompareOrdinal(left.Name, right.Name));
            return entries;
        }
        catch (IOException exception) when (File.Exists(fullPath))
        {
            return FileInTheWay(relativePath, relativePath, exception.HResult);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Failure(exception, relativePath);
        }
    }

    /// <summary>Throws unless the text is a normalised relative path: one that some <see cref="VfsPath"/> has.</summary>
    /// <remarks>
    /// <see cref="InMemoryFileSystem"/>'s rule: the shortest prefix a path can have is a one-letter scheme and
    /// <c>://</c>, so prepending one accepts exactly the relative paths of every valid <see cref="VfsPath"/>, length
    /// limit included, and nothing else.
    /// </remarks>
    /// <param name="relativePath">The argument.</param>
    /// <exception cref="ArgumentNullException"><paramref name="relativePath"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="relativePath"/> isn't a normalised relative path.</exception>
    private static void ThrowIfNotNormalised(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        if (relativePath.Length != 0
            && !(VfsPath.TryParse(string.Concat("r://", relativePath), out VfsPath path)
                && string.Equals(path.RelativePath, relativePath, StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "The path is not a normalised VFS relative path (VfsPath.RelativePath): segments joined by '/', with no leading, trailing or repeated '/', no '.' or '..' segment, and only names and lengths a VfsPath allows.",
                nameof(relativePath));
        }
    }

    /// <summary>What is at a full path: nothing (also when a parent is missing or a file), a file or a directory.</summary>
    /// <param name="fullPath">A path under the root.</param>
    /// <returns>The kind.</returns>
    /// <exception cref="IOException">The OS can't answer.</exception>
    /// <exception cref="UnauthorizedAccessException">The OS denies looking.</exception>
    private static VfsEntryKind KindOf(string fullPath)
    {
        FileAttributes attributes = new FileInfo(fullPath).Attributes;
        return (int)attributes == -1 ? VfsEntryKind.None
            : (attributes & FileAttributes.Directory) != 0 ? VfsEntryKind.Directory
            : VfsEntryKind.File;
    }

    /// <summary>
    /// The length <see cref="List"/> gives a file entry: a symbolic link's final target's length, through any chain of
    /// links, or the length the listing read.
    /// </summary>
    /// <remarks>
    /// Whether the entry is a link comes from the attributes the listing read, which a removal can't change; asking the
    /// OS again (<see cref="FileSystemInfo.LinkTarget"/>) answers "not a link" once the link is gone, and the
    /// listing's length is then the link's own. A link's own length is its target text on POSIX and 0 on Windows, so
    /// the final target is measured instead. A reparse point that the OS doesn't resolve as a link (on Windows, a tag
    /// other than a symbolic link's or a mount point's), or an entry no longer a link when it is resolved, keeps the
    /// listing's length.
    /// </remarks>
    /// <param name="file">A file entry from the listing.</param>
    /// <returns>
    /// The length; 0 for a link that leads to no file: its target is missing or not a file, or the OS can't follow
    /// the links to the end (a loop, or a link it may not read).
    /// </returns>
    /// <exception cref="FileNotFoundException">The entry is a link that is gone.</exception>
    private static long ListedLength(FileInfo file)
    {
        if ((file.Attributes & FileAttributes.ReparsePoint) == 0)
        {
            return file.Length;
        }

        FileSystemInfo? target;
        try
        {
            target = file.ResolveLinkTarget(returnFinalTarget: true);
        }
        catch (Exception exception) when (exception is IOException and not FileNotFoundException or UnauthorizedAccessException)
        {
            // The links can't be followed to a file, so there is no file to measure; one link isn't a failure of List.
            return 0;
        }

        return target is null ? file.Length
            : target is FileInfo { Exists: true } final ? final.Length
            : 0;
    }

    /// <summary>Maps an OS exception to the <see cref="IFileSystem"/> kinds, keeping its message and code.</summary>
    /// <param name="exception">An <see cref="IOException"/> or an <see cref="UnauthorizedAccessException"/>.</param>
    /// <param name="relativePath">The path the message quotes.</param>
    /// <returns>The error.</returns>
    private static EngineError Failure(Exception exception, string relativePath) => new(
        exception switch
        {
            FileNotFoundException or DirectoryNotFoundException => ErrorKind.NotFound,
            UnauthorizedAccessException => ErrorKind.Unavailable,
            _ => ErrorKind.Io,
        },
        $"'{relativePath}': {exception.Message}",
        exception.HResult);

    private static EngineError IsADirectory(Exception exception, string relativePath) =>
        new(ErrorKind.NotFound, $"'{relativePath}' is a directory, not a file.", exception.HResult);

    private static EngineError FileInTheWay(string file, string relativePath, int nativeCode) => new(
        ErrorKind.NotFound,
        string.Equals(file, relativePath, StringComparison.Ordinal)
            ? $"'{relativePath}' is a file, not a directory."
            : $"'{file}' is a file, so '{relativePath}' can't be inside it.",
        nativeCode);

    private static EngineError RootIsGone(string relativePath, int nativeCode) =>
        new(ErrorKind.NotFound, $"The root directory no longer exists, so '{relativePath}' can't be made.", nativeCode);

    private static EngineError RootIsNotAFile() => new(ErrorKind.NotFound, "'' is the root directory, not a file.");

    private static EngineError ReadOnly(string relativePath) =>
        new(ErrorKind.Unsupported, $"The file system is read-only, so '{relativePath}' can't be changed.");

    private static FileStream NewTemporary(string temporaryPath) =>
        new(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);

    private static void DeleteTemporary(string temporaryPath)
    {
        try
        {
            File.Delete(temporaryPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The write reports its own failure; a temporary file that can't be deleted is listed like any file.
        }
    }

    private string GetFullPath(string relativePath) => relativePath.Length == 0
        ? _root
        : Path.Join(_root, Path.DirectorySeparatorChar == '/' ? relativePath : relativePath.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Creates the temporary file, creating the target's missing parent directories if it has to.</summary>
    /// <param name="relativePath">The target's relative path.</param>
    /// <param name="directory">The target's full parent directory.</param>
    /// <param name="temporaryPath">The temporary file's full path.</param>
    /// <returns>The open stream, which the caller disposes, or the failure.</returns>
    private Result<FileStream> CreateTemporary(string relativePath, string directory, string temporaryPath)
    {
        try
        {
            try
            {
                return NewTemporary(temporaryPath);
            }
            catch (DirectoryNotFoundException missing)
            {
                // Directory.CreateDirectory creates every missing ancestor, so it would bring a removed root back (it
                // still does if the root is removed after this check, a window the OS gives no way to close); and a
                // file on the way is reported as one.
                if (!Directory.Exists(_root))
                {
                    return RootIsGone(relativePath, missing.HResult);
                }

                if (FindFile(relativePath, includeSelf: false) is { } file)
                {
                    return FileInTheWay(file, relativePath, missing.HResult);
                }

                Directory.CreateDirectory(directory);
                return NewTemporary(temporaryPath);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Failure(exception, relativePath);
        }
    }

    /// <summary>Finds the first file, from the top, among a path's ancestors (and the path itself if asked).</summary>
    /// <param name="relativePath">A normalised relative path.</param>
    /// <param name="includeSelf">Whether the path itself is a candidate.</param>
    /// <returns>
    /// The file's relative path, or <see langword="null"/> if a candidate is missing first, or the OS can't say (the
    /// caller then reports the failure it already has).
    /// </returns>
    private string? FindFile(string relativePath, bool includeSelf)
    {
        for (int end = relativePath.IndexOf('/'); end >= 0 || includeSelf; end = relativePath.IndexOf('/', end + 1))
        {
            string candidate = end < 0 ? relativePath : relativePath[..end];
            VfsEntryKind kind;
            try
            {
                kind = KindOf(GetFullPath(candidate));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return null;
            }

            if (kind != VfsEntryKind.Directory || end < 0)
            {
                return kind == VfsEntryKind.File ? candidate : null;
            }
        }

        return null;
    }
}
