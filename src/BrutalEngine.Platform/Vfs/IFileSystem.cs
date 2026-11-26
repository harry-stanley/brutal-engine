using System;
using System.Collections.Generic;
using System.IO;
using BrutalEngine.Core;

namespace BrutalEngine.Platform.Vfs;

/// <summary>
/// A file system that a <c>VirtualFileSystem</c> mounts at a scheme: a directory on disk, an in-memory tree, and later
/// a pack.
/// </summary>
/// <remarks>
/// <para>
/// <b>Paths:</b> every <c>relativePath</c> is a <see cref="VfsPath.RelativePath"/>: validated and normalised, segments
/// joined by <c>/</c>, no leading or trailing <c>/</c>, no <c>.</c> or <c>..</c> segment. The empty string is the root,
/// which is always a directory. Implementations compare paths ordinally (case-sensitive), never resolve them against
/// anything outside their own root, and throw <see cref="ArgumentException"/> for text that isn't such a path, so a
/// caller that bypasses the virtual file system can't escape the root either.
/// </para>
/// <para>
/// <b>Failures</b> are <see cref="Result"/>s with these kinds: a missing entry, or a file where a directory was
/// expected or the reverse, is <see cref="ErrorKind.NotFound"/>; a write, delete or create on a read-only file system,
/// or a file over <see cref="Array.MaxLength"/> bytes for <see cref="ReadAllBytes"/>, is
/// <see cref="ErrorKind.Unsupported"/>; access denied by the OS is <see cref="ErrorKind.Unavailable"/>; any other I/O
/// error, including deleting a non-empty directory, is <see cref="ErrorKind.Io"/> with the OS message and its
/// <see cref="Exception.HResult"/> as <see cref="EngineError.NativeCode"/> (0 when no OS call failed). On a read-only
/// file system, writes, deletes and creates fail with <see cref="ErrorKind.Unsupported"/> before anything else is
/// checked. Implementations don't throw for these failures; misuse does throw: a <see langword="null"/> path
/// (<see cref="ArgumentNullException"/>) or a path that isn't normalised (<see cref="ArgumentException"/>).
/// </para>
/// <para>
/// <b>Threads:</b> implementations must be safe to call from any thread, concurrently, because <c>VirtualFileSystem</c>
/// forwards calls from whichever thread makes them (asset I/O threads read while the main thread mounts). Concurrent
/// writers of one path race; one of them wins, and readers see either the old or the new contents, never a mix.
/// </para>
/// <para>
/// <b>Ownership:</b> a mount doesn't own its file system. The type isn't disposable; whoever creates an implementation
/// keeps any resources it holds and releases them after unmounting it.
/// </para>
/// <para>
/// <b>Allocation:</b> file I/O is not a hot path. Results, byte arrays, streams, entry lists and error messages
/// allocate. See docs/design/platform.md section 5, "Mounts and operations".
/// </para>
/// </remarks>
public interface IFileSystem
{
    /// <summary>Whether the file system refuses every write, delete and create.</summary>
    /// <value>
    /// <see langword="true"/> if <see cref="WriteAllBytes"/>, <see cref="Delete"/> and <see cref="CreateDirectory"/>
    /// always fail with <see cref="ErrorKind.Unsupported"/>. It may change from <see langword="false"/> to
    /// <see langword="true"/> during the file system's life (an in-memory tree frozen after seeding), never back.
    /// Any thread. Allocates nothing.
    /// </value>
    public bool IsReadOnly { get; }

    /// <summary>Says what is at a path: a file, a directory, or nothing.</summary>
    /// <param name="relativePath">A normalised relative path; empty for the root.</param>
    /// <returns>
    /// The entry's kind, or <see cref="VfsEntryKind.None"/> when nothing is there: a missing entry is a successful
    /// answer here, not a <see cref="ErrorKind.NotFound"/> failure. Fails with <see cref="ErrorKind.Unavailable"/> or
    /// <see cref="ErrorKind.Io"/> when the OS can't answer. Allocates only on failure.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="relativePath"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="relativePath"/> isn't a normalised relative path.</exception>
    public Result<VfsEntryKind> GetEntryKind(string relativePath);

    /// <summary>Reads a whole file into a new array.</summary>
    /// <param name="relativePath">A normalised relative path to a file.</param>
    /// <returns>
    /// A new array the caller owns, holding the file's contents at the time of the call. Fails with
    /// <see cref="ErrorKind.NotFound"/> if there is no file there (nothing, or a directory such as the root),
    /// <see cref="ErrorKind.Unsupported"/> if the file is longer than <see cref="Array.MaxLength"/> bytes, and
    /// <see cref="ErrorKind.Unavailable"/> or <see cref="ErrorKind.Io"/> when the OS refuses. Allocates the array.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="relativePath"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="relativePath"/> isn't a normalised relative path.</exception>
    public Result<byte[]> ReadAllBytes(string relativePath);

    /// <summary>Opens a file for reading.</summary>
    /// <param name="relativePath">A normalised relative path to a file.</param>
    /// <returns>
    /// A readable stream positioned at the start, which the caller owns and must dispose. It may or may not be
    /// seekable, and it never allows writing. Fails with <see cref="ErrorKind.NotFound"/> if there is no file there,
    /// and <see cref="ErrorKind.Unavailable"/> or <see cref="ErrorKind.Io"/> when the OS refuses. Allocates the
    /// stream.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="relativePath"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="relativePath"/> isn't a normalised relative path.</exception>
    public Result<Stream> OpenRead(string relativePath);

    /// <summary>Creates or replaces a file with the given contents, creating any missing parent directories.</summary>
    /// <remarks>
    /// The replace is atomic: a concurrent reader, or a crash, sees the old contents or the new ones, never a part.
    /// The file system copies <paramref name="contents"/> before returning and keeps no reference to the caller's
    /// memory.
    /// </remarks>
    /// <param name="relativePath">A normalised relative path to a file.</param>
    /// <param name="contents">The bytes to write; may be empty.</param>
    /// <returns>
    /// Success, or <see cref="ErrorKind.Unsupported"/> on a read-only file system, <see cref="ErrorKind.NotFound"/>
    /// if the path is a directory (the root always is) or one of its parents is a file, and
    /// <see cref="ErrorKind.Unavailable"/> or <see cref="ErrorKind.Io"/> when the OS refuses. Allocates.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="relativePath"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="relativePath"/> isn't a normalised relative path.</exception>
    public Result WriteAllBytes(string relativePath, ReadOnlySpan<byte> contents);

    /// <summary>Deletes a file, or an empty directory.</summary>
    /// <param name="relativePath">A normalised relative path to a file or a directory.</param>
    /// <returns>
    /// Success, or <see cref="ErrorKind.Unsupported"/> on a read-only file system or for the root, which can't be
    /// deleted, <see cref="ErrorKind.NotFound"/> if nothing is there, <see cref="ErrorKind.Io"/> if the directory
    /// isn't empty, and <see cref="ErrorKind.Unavailable"/> or <see cref="ErrorKind.Io"/> when the OS refuses.
    /// Allocates on failure.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="relativePath"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="relativePath"/> isn't a normalised relative path.</exception>
    public Result Delete(string relativePath);

    /// <summary>Creates a directory and any missing parents; succeeds if it already exists.</summary>
    /// <param name="relativePath">A normalised relative path; the root always exists, so creating it succeeds.</param>
    /// <returns>
    /// Success, or <see cref="ErrorKind.Unsupported"/> on a read-only file system, <see cref="ErrorKind.NotFound"/>
    /// if the path or one of its parents is a file, and <see cref="ErrorKind.Unavailable"/> or
    /// <see cref="ErrorKind.Io"/> when the OS refuses. Allocates on failure.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="relativePath"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="relativePath"/> isn't a normalised relative path.</exception>
    public Result CreateDirectory(string relativePath);

    /// <summary>Lists a directory's direct children, sorted by name ordinally.</summary>
    /// <param name="relativePath">A normalised relative path to a directory; empty for the root.</param>
    /// <returns>
    /// A new list the caller owns, one level deep, sorted with <see cref="StringComparer.Ordinal"/> on
    /// <see cref="VfsEntry.Name"/>; empty for an empty directory. Fails with <see cref="ErrorKind.NotFound"/> if
    /// there is no directory there (nothing, or a file), and <see cref="ErrorKind.Unavailable"/> or
    /// <see cref="ErrorKind.Io"/> when the OS refuses. Allocates the list and its names.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="relativePath"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="relativePath"/> isn't a normalised relative path.</exception>
    public Result<IReadOnlyList<VfsEntry>> List(string relativePath);
}
