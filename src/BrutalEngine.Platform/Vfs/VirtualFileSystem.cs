using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using BrutalEngine.Core;

namespace BrutalEngine.Platform.Vfs;

/// <summary>
/// Routes <see cref="VfsPath"/> operations to the file system mounted at each path's scheme: <c>res://</c> to the
/// game's read-only assets, <c>user://</c> to the user's data directory, any other scheme to what a test or tool
/// mounts.
/// </summary>
/// <remarks>
/// <para>
/// <b>Routing:</b> each operation finds the mount whose scheme equals the path's <see cref="VfsPath.Scheme"/>
/// (ordinal, so <c>res</c> never matches <c>re</c> or <c>res2</c>) and calls the same operation on its
/// <see cref="IFileSystem"/> with the path's normalised <see cref="VfsPath.RelativePath"/>. The file system's
/// <see cref="Result"/> is returned unchanged, so failures keep their kind, message and native code (see the error
/// table in docs/design/platform.md section 5, "Mounts and operations"). A path whose scheme has nothing mounted fails
/// with <see cref="ErrorKind.NotFound"/> without calling any file system.
/// </para>
/// <para>
/// <b>Misuse throws:</b> <c>default(VfsPath)</c> (<see cref="ArgumentException"/>), a <see langword="null"/> argument
/// (<see cref="ArgumentNullException"/>), an invalid scheme (<see cref="ArgumentException"/>) and mounting a scheme
/// twice (<see cref="InvalidOperationException"/>). Exceptions a file system throws reach the caller unchanged.
/// </para>
/// <para>
/// <b>Threads:</b> every member is safe on any thread: asset I/O threads read while the main thread mounts. The mounts
/// are an immutable array that <see cref="Mount"/> and <see cref="Unmount"/> replace under a lock; every other member
/// reads the current array without locking, so an operation racing a mount change routes by the mounts either before
/// or after it, never a mix. An operation already running in a file system when it is unmounted completes there.
/// </para>
/// <para>
/// <b>Ownership:</b> the virtual file system doesn't own what it mounts. It isn't disposable, never disposes a file
/// system, and <see cref="Unmount"/> only removes the mount: whoever created a file system keeps its resources.
/// </para>
/// <para>
/// <b>Allocation:</b> <see cref="Mount"/> and <see cref="Unmount"/> allocate a new mount array; an operation with
/// nothing mounted at its scheme allocates its error message; everything else allocates only what the file system
/// does. File I/O is not a hot path.
/// </para>
/// </remarks>
public sealed class VirtualFileSystem
{
    // An invalid scheme's message quotes at most this many characters of it, as VfsPath's messages do.
    private const int MaxQuotedChars = 256;

    private readonly Lock _gate = new();
    private volatile MountPoint[] _mounts = [];

    /// <summary>Mounts a file system at a scheme, so paths such as <c>scheme://a/b</c> reach it.</summary>
    /// <param name="scheme">
    /// The scheme without <c>://</c>: a lowercase ASCII letter, then up to 31 lowercase ASCII letters, digits or
    /// <c>-</c>, exactly as <see cref="VfsPath"/> accepts.
    /// </param>
    /// <param name="fileSystem">The file system to mount. The caller keeps ownership.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="scheme"/> or <paramref name="fileSystem"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="scheme"/> isn't a valid scheme. The message quotes it: at most 256 characters, then
    /// <c>...</c>, with control characters and lone surrogates written as <c>\uXXXX</c>.
    /// </exception>
    /// <exception cref="InvalidOperationException">Something is already mounted at <paramref name="scheme"/>.</exception>
    public void Mount(string scheme, IFileSystem fileSystem)
    {
        ArgumentNullException.ThrowIfNull(scheme);
        ArgumentNullException.ThrowIfNull(fileSystem);
        if (!IsValidScheme(scheme))
        {
            throw new ArgumentException(
                $"'{Quote(scheme)}' is not a valid scheme: it must be 1 to 32 characters, a lowercase ASCII letter, then lowercase ASCII letters, digits or '-', without \"://\".",
                nameof(scheme));
        }

        lock (_gate)
        {
            MountPoint[] mounts = _mounts;
            if (IndexOf(mounts, scheme) >= 0)
            {
                throw new InvalidOperationException($"A file system is already mounted at '{scheme}://'; unmount it first.");
            }

            var next = new MountPoint[mounts.Length + 1];
            mounts.CopyTo(next, 0);
            next[^1] = new MountPoint(scheme, fileSystem);
            _mounts = next;
        }
    }

    /// <summary>Removes the mount at a scheme, without disposing or otherwise touching its file system.</summary>
    /// <param name="scheme">The scheme without <c>://</c>.</param>
    /// <returns>
    /// <see langword="true"/> if a file system was mounted there and has been removed; <see langword="false"/> if
    /// nothing was mounted there, which includes every invalid scheme.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="scheme"/> is <see langword="null"/>.</exception>
    public bool Unmount(string scheme)
    {
        ArgumentNullException.ThrowIfNull(scheme);
        lock (_gate)
        {
            MountPoint[] mounts = _mounts;
            int index = IndexOf(mounts, scheme);
            if (index < 0)
            {
                return false;
            }

            var next = new MountPoint[mounts.Length - 1];
            Array.Copy(mounts, 0, next, 0, index);
            Array.Copy(mounts, index + 1, next, index, next.Length - index);
            _mounts = next;
            return true;
        }
    }

    /// <summary>Whether a file system is mounted at a scheme.</summary>
    /// <param name="scheme">The scheme without <c>://</c>.</param>
    /// <returns>
    /// <see langword="true"/> if something is mounted there now; <see langword="false"/> otherwise, including for every
    /// invalid scheme. Allocates nothing.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="scheme"/> is <see langword="null"/>.</exception>
    public bool IsMounted(string scheme)
    {
        ArgumentNullException.ThrowIfNull(scheme);
        return IndexOf(_mounts, scheme) >= 0;
    }

    /// <summary>Whether a file or a directory exists at a path.</summary>
    /// <param name="path">The path.</param>
    /// <returns>
    /// <see langword="true"/> if the mount's <see cref="IFileSystem.GetEntryKind"/> reports a file or a directory,
    /// <see langword="false"/> if it reports nothing there; its failure otherwise, or <see cref="ErrorKind.NotFound"/>
    /// if nothing is mounted at the scheme.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is <c>default(VfsPath)</c>.</exception>
    public Result<bool> Exists(VfsPath path)
    {
        Result<VfsEntryKind> kind = GetEntryKind(path);
        return kind.TryGetValue(out VfsEntryKind value) ? value != VfsEntryKind.None : kind.Error;
    }

    /// <summary>Says what is at a path: a file, a directory, or nothing (<see cref="VfsEntryKind.None"/>).</summary>
    /// <param name="path">The path.</param>
    /// <returns>
    /// The mount's <see cref="IFileSystem.GetEntryKind"/> result, or <see cref="ErrorKind.NotFound"/> if nothing is
    /// mounted at the scheme.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is <c>default(VfsPath)</c>.</exception>
    public Result<VfsEntryKind> GetEntryKind(VfsPath path) =>
        TryFind(path, out IFileSystem? fileSystem, out EngineError error)
            ? fileSystem.GetEntryKind(path.RelativePath)
            : error;

    /// <summary>Reads a whole file into a new array the caller owns.</summary>
    /// <param name="path">The path of a file.</param>
    /// <returns>
    /// The mount's <see cref="IFileSystem.ReadAllBytes"/> result, or <see cref="ErrorKind.NotFound"/> if nothing is
    /// mounted at the scheme.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is <c>default(VfsPath)</c>.</exception>
    public Result<byte[]> ReadAllBytes(VfsPath path) =>
        TryFind(path, out IFileSystem? fileSystem, out EngineError error)
            ? fileSystem.ReadAllBytes(path.RelativePath)
            : error;

    /// <summary>Opens a file for reading. The caller owns the stream and must dispose it.</summary>
    /// <param name="path">The path of a file.</param>
    /// <returns>
    /// The mount's <see cref="IFileSystem.OpenRead"/> result, or <see cref="ErrorKind.NotFound"/> if nothing is
    /// mounted at the scheme.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is <c>default(VfsPath)</c>.</exception>
    public Result<Stream> OpenRead(VfsPath path) =>
        TryFind(path, out IFileSystem? fileSystem, out EngineError error)
            ? fileSystem.OpenRead(path.RelativePath)
            : error;

    /// <summary>
    /// Creates or atomically replaces a file with the given contents, creating any missing parent directories.
    /// </summary>
    /// <param name="path">The path of the file.</param>
    /// <param name="contents">The bytes to write; the file system copies them before returning.</param>
    /// <returns>
    /// The mount's <see cref="IFileSystem.WriteAllBytes"/> result, or <see cref="ErrorKind.NotFound"/> if nothing is
    /// mounted at the scheme.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is <c>default(VfsPath)</c>.</exception>
    public Result WriteAllBytes(VfsPath path, ReadOnlySpan<byte> contents) =>
        TryFind(path, out IFileSystem? fileSystem, out EngineError error)
            ? fileSystem.WriteAllBytes(path.RelativePath, contents)
            : error;

    /// <summary>Deletes a file, or an empty directory.</summary>
    /// <param name="path">The path of a file or a directory.</param>
    /// <returns>
    /// The mount's <see cref="IFileSystem.Delete"/> result, or <see cref="ErrorKind.NotFound"/> if nothing is mounted
    /// at the scheme.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is <c>default(VfsPath)</c>.</exception>
    public Result Delete(VfsPath path) =>
        TryFind(path, out IFileSystem? fileSystem, out EngineError error)
            ? fileSystem.Delete(path.RelativePath)
            : error;

    /// <summary>Creates a directory and any missing parents; succeeds if it already exists.</summary>
    /// <param name="path">The path of the directory.</param>
    /// <returns>
    /// The mount's <see cref="IFileSystem.CreateDirectory"/> result, or <see cref="ErrorKind.NotFound"/> if nothing is
    /// mounted at the scheme.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is <c>default(VfsPath)</c>.</exception>
    public Result CreateDirectory(VfsPath path) =>
        TryFind(path, out IFileSystem? fileSystem, out EngineError error)
            ? fileSystem.CreateDirectory(path.RelativePath)
            : error;

    /// <summary>Lists a directory's direct children, sorted by name ordinally.</summary>
    /// <param name="directory">The path of a directory; <c>scheme://</c> for the mount's root.</param>
    /// <returns>
    /// The mount's <see cref="IFileSystem.List"/> result, or <see cref="ErrorKind.NotFound"/> if nothing is mounted at
    /// the scheme.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="directory"/> is <c>default(VfsPath)</c>.</exception>
    public Result<IReadOnlyList<VfsEntry>> List(VfsPath directory) =>
        TryFind(directory, out IFileSystem? fileSystem, out EngineError error)
            ? fileSystem.List(directory.RelativePath)
            : error;

    /// <summary>
    /// Whether text is a scheme: the one <see cref="VfsPath"/> accepts, so there is one scheme rule. A path made of
    /// the scheme and <c>://</c> must parse to that scheme and the mount root.
    /// </summary>
    /// <param name="scheme">The candidate scheme.</param>
    /// <returns><see langword="true"/> if <paramref name="scheme"/> is a valid scheme.</returns>
    private static bool IsValidScheme(string scheme) =>
        VfsPath.TryParse(string.Concat(scheme, "://"), out VfsPath root)

        // Defensive: the parser splits at the first ':', so a parse that succeeds already gives this scheme and the
        // root. These keep the rule true if the parser ever changes.
        && string.Equals(root.Scheme, scheme, StringComparison.Ordinal)
        && root.RelativePath.Length == 0;

    /// <summary>
    /// The text as an error message quotes it, the way <see cref="VfsPath"/>'s messages do: at most
    /// <see cref="MaxQuotedChars"/> characters, then <c>...</c>, with control characters and lone surrogates written as
    /// <c>\uXXXX</c>, so a message stays short and printable.
    /// </summary>
    /// <param name="text">The rejected text.</param>
    /// <returns>The quoted form, without the quotes.</returns>
    private static string Quote(string text)
    {
        ReadOnlySpan<char> shown = text.AsSpan(0, Math.Min(text.Length, MaxQuotedChars));
        StringBuilder quoted = new(shown.Length + 8);
        for (int i = 0; i < shown.Length; i++)
        {
            char c = shown[i];
            bool pairStart = char.IsHighSurrogate(c) && i + 1 < shown.Length && char.IsLowSurrogate(shown[i + 1]);
            bool pairEnd = char.IsLowSurrogate(c) && i > 0 && char.IsHighSurrogate(shown[i - 1]);
            if (char.IsControl(c) || (char.IsSurrogate(c) && !pairStart && !pairEnd))
            {
                quoted.Append(CultureInfo.InvariantCulture, $"\\u{(int)c:X4}");
            }
            else
            {
                quoted.Append(c);
            }
        }

        if (shown.Length < text.Length)
        {
            quoted.Append("...");
        }

        return quoted.ToString();
    }

    private static int IndexOf(MountPoint[] mounts, string scheme)
    {
        for (int i = 0; i < mounts.Length; i++)
        {
            if (string.Equals(mounts[i].Scheme, scheme, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Finds the file system mounted at a path's scheme, reading the mount array once, without a lock.</summary>
    /// <param name="path">The argument.</param>
    /// <param name="fileSystem">The mounted file system, when found.</param>
    /// <param name="error">The <see cref="ErrorKind.NotFound"/> failure, when nothing is mounted.</param>
    /// <param name="paramName">The argument's name, filled in by the compiler.</param>
    /// <returns><see langword="true"/> if a file system is mounted at the scheme.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is <c>default(VfsPath)</c>.</exception>
    private bool TryFind(
        VfsPath path,
        [NotNullWhen(true)] out IFileSystem? fileSystem,
        out EngineError error,
        [CallerArgumentExpression(nameof(path))] string? paramName = null)
    {
        VfsPath.ThrowIfDefault(path, paramName);
        MountPoint[] mounts = _mounts;
        int index = IndexOf(mounts, path.Scheme);
        if (index >= 0)
        {
            fileSystem = mounts[index].FileSystem;
            error = default;
            return true;
        }

        fileSystem = null;
        error = new EngineError(ErrorKind.NotFound, $"No file system is mounted at '{path.Scheme}://', so '{path}' can't be reached.");
        return false;
    }

    /// <summary>One mount: a scheme and the file system behind it. Immutable, so the array can be read unlocked.</summary>
    /// <param name="Scheme">The scheme without <c>://</c>.</param>
    /// <param name="FileSystem">The mounted file system, not owned.</param>
    private sealed record MountPoint(string Scheme, IFileSystem FileSystem);
}
