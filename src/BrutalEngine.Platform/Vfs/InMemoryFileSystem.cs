using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using BrutalEngine.Core;

namespace BrutalEngine.Platform.Vfs;

/// <summary>
/// A thread-safe file system held in memory, for tests and generated content: mount it under any scheme, seed it by
/// writing, then optionally <see cref="Freeze"/> it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Contents:</b> a tree of directories and files under an always-present root (<c>""</c>). Every write stores a copy
/// of the caller's bytes; <see cref="ReadAllBytes"/> returns a new copy and <see cref="OpenRead"/> a read-only,
/// non-exposable <see cref="MemoryStream"/> over its own copy, so no caller ever shares memory with the file system or
/// with another caller. <see cref="WriteAllBytes"/> creates missing parent directories, and replacing a file swaps its
/// contents in one step. <see cref="List"/> sorts names with <see cref="StringComparer.Ordinal"/>.
/// </para>
/// <para>
/// <b>Failures</b> follow <see cref="IFileSystem"/>. A missing entry, or a file where a directory is needed or the
/// reverse, is <see cref="ErrorKind.NotFound"/>. A write, delete or create on a read-only file system, and deleting the
/// root, is <see cref="ErrorKind.Unsupported"/>. Deleting a non-empty directory is <see cref="ErrorKind.Io"/> with
/// <see cref="EngineError.NativeCode"/> 0, because no OS call is involved. No other OS errors can occur. Messages quote
/// the relative path. A failed operation changes nothing. A <see langword="null"/> path throws
/// <see cref="ArgumentNullException"/>, and text that isn't a normalised <see cref="VfsPath.RelativePath"/> throws
/// <see cref="ArgumentException"/>, before the file system is looked at.
/// </para>
/// <para>
/// <b>Threads:</b> every member is safe on any thread. One lock guards the tree, and each operation holds it once, so
/// operations are atomic with respect to each other: a reader sees a file's old contents or its new ones, never a mix.
/// Byte arrays are copied outside the lock.
/// </para>
/// <para>
/// <b>Ownership and allocation:</b> the file system holds only managed memory and isn't disposable; drop it when no
/// longer mounted. Every operation allocates (copies, streams, entry lists, path checks and error messages); file I/O
/// is not a hot path. See docs/design/platform.md section 5, "Mounts and operations".
/// </para>
/// </remarks>
public sealed class InMemoryFileSystem : IFileSystem
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Node> _nodes = new(StringComparer.Ordinal);
    private volatile bool _isReadOnly;

    /// <summary>Creates an empty file system: a root directory with nothing in it.</summary>
    /// <param name="isReadOnly">
    /// <see langword="true"/> to refuse every write from the start (the file system then stays empty);
    /// <see langword="false"/>, the default, to allow writes until <see cref="Freeze"/> is called.
    /// </param>
    public InMemoryFileSystem(bool isReadOnly = false)
    {
        _nodes.Add(string.Empty, Node.CreateDirectory());
        _isReadOnly = isReadOnly;
    }

    /// <summary>Whether the file system refuses every write, delete and create.</summary>
    /// <value>
    /// <see langword="true"/> if it was created read-only or <see cref="Freeze"/> has been called; it never changes
    /// back. Any thread. Allocates nothing.
    /// </value>
    public bool IsReadOnly => _isReadOnly;

    /// <summary>Makes the file system read-only from now on, typically once a test has seeded it.</summary>
    /// <remarks>
    /// An operation that started before the call either completes or fails with <see cref="ErrorKind.Unsupported"/>;
    /// every write, delete and create that starts after it returns fails with <see cref="ErrorKind.Unsupported"/>.
    /// Reads keep working. Calling it again does nothing. Any thread. Allocates nothing.
    /// </remarks>
    public void Freeze()
    {
        lock (_gate)
        {
            _isReadOnly = true;
        }
    }

    /// <inheritdoc/>
    public Result<VfsEntryKind> GetEntryKind(string relativePath)
    {
        ThrowIfNotNormalised(relativePath);
        lock (_gate)
        {
            return !_nodes.TryGetValue(relativePath, out Node? node) ? VfsEntryKind.None
                : node.IsDirectory ? VfsEntryKind.Directory
                : VfsEntryKind.File;
        }
    }

    /// <inheritdoc/>
    public Result<byte[]> ReadAllBytes(string relativePath)
    {
        ThrowIfNotNormalised(relativePath);
        Result<byte[]> stored = GetContents(relativePath);
        return stored.TryGetValue(out byte[]? contents) ? contents.AsSpan().ToArray() : stored;
    }

    /// <inheritdoc/>
    public Result<Stream> OpenRead(string relativePath)
    {
        ThrowIfNotNormalised(relativePath);
        Result<byte[]> stored = GetContents(relativePath);
        return stored.TryGetValue(out byte[]? contents)
            ? new MemoryStream(contents.AsSpan().ToArray(), writable: false)
            : stored.Error;
    }

    /// <inheritdoc/>
    public Result WriteAllBytes(string relativePath, ReadOnlySpan<byte> contents)
    {
        ThrowIfNotNormalised(relativePath);
        byte[] copy = contents.ToArray();
        lock (_gate)
        {
            if (_isReadOnly)
            {
                return ReadOnly(relativePath);
            }

            if (relativePath.Length == 0)
            {
                return NotFound("'' is the root directory, not a file.");
            }

            if (FindFileAncestor(relativePath) is { } file)
            {
                return NotFound($"'{file}' is a file, so '{relativePath}' can't be inside it.");
            }

            if (_nodes.TryGetValue(relativePath, out Node? existing))
            {
                if (existing.IsDirectory)
                {
                    return NotFound($"'{relativePath}' is a directory, not a file.");
                }

                existing.Contents = copy;
                return Result.Success;
            }

            AddNode(relativePath, Node.CreateFile(copy));
            return Result.Success;
        }
    }

    /// <inheritdoc/>
    public Result Delete(string relativePath)
    {
        ThrowIfNotNormalised(relativePath);
        lock (_gate)
        {
            if (_isReadOnly)
            {
                return ReadOnly(relativePath);
            }

            if (relativePath.Length == 0)
            {
                return new EngineError(ErrorKind.Unsupported, "'' is the root directory, which can't be deleted.");
            }

            if (!_nodes.TryGetValue(relativePath, out Node? node))
            {
                return NotFound($"Nothing exists at '{relativePath}'.");
            }

            if (node.Children is { Count: > 0 })
            {
                return new EngineError(ErrorKind.Io, $"The directory '{relativePath}' is not empty.");
            }

            _nodes.Remove(relativePath);
            int slash = relativePath.LastIndexOf('/');
            string parent = slash < 0 ? string.Empty : relativePath[..slash];
            _nodes[parent].Children!.Remove(relativePath[(slash + 1)..]);
            return Result.Success;
        }
    }

    /// <inheritdoc/>
    public Result CreateDirectory(string relativePath)
    {
        ThrowIfNotNormalised(relativePath);
        lock (_gate)
        {
            if (_isReadOnly)
            {
                return ReadOnly(relativePath);
            }

            if (FindFileAncestor(relativePath) is { } file)
            {
                return NotFound($"'{file}' is a file, so '{relativePath}' can't be inside it.");
            }

            if (_nodes.TryGetValue(relativePath, out Node? existing))
            {
                return existing.IsDirectory
                    ? Result.Success
                    : NotFound($"'{relativePath}' is a file, not a directory.");
            }

            AddNode(relativePath, Node.CreateDirectory());
            return Result.Success;
        }
    }

    /// <inheritdoc/>
    public Result<IReadOnlyList<VfsEntry>> List(string relativePath)
    {
        ThrowIfNotNormalised(relativePath);
        lock (_gate)
        {
            if (!_nodes.TryGetValue(relativePath, out Node? node))
            {
                return NotFound($"No directory exists at '{relativePath}'.");
            }

            if (node.Children is not { } children)
            {
                return NotFound($"'{relativePath}' is a file, not a directory.");
            }

            var entries = new VfsEntry[children.Count];
            int index = 0;
            foreach (string name in children)
            {
                Node child = _nodes[relativePath.Length == 0 ? name : string.Concat(relativePath, "/", name)];
                entries[index++] = child.IsDirectory
                    ? new VfsEntry(name, VfsEntryKind.Directory, 0)
                    : new VfsEntry(name, VfsEntryKind.File, child.Contents!.Length);
            }

            return entries;
        }
    }

    /// <summary>Throws unless the text is a normalised relative path: one that some <see cref="VfsPath"/> has.</summary>
    /// <remarks>
    /// The shortest prefix a path can have is a one-letter scheme and <c>://</c>, so prepending one accepts exactly
    /// the relative paths of every valid <see cref="VfsPath"/>, length limit included, and nothing else: parsing would
    /// normalise anything else into different text, or reject it.
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

    private static EngineError NotFound(string message) => new(ErrorKind.NotFound, message);

    private static EngineError ReadOnly(string relativePath) =>
        new(ErrorKind.Unsupported, $"The file system is read-only, so '{relativePath}' can't be changed.");

    /// <summary>Finds the stored contents of a file under the lock; the caller copies them after releasing it.</summary>
    /// <param name="relativePath">A normalised relative path.</param>
    /// <returns>The stored array, which is never written to again, or the <see cref="ErrorKind.NotFound"/> failure.</returns>
    private Result<byte[]> GetContents(string relativePath)
    {
        lock (_gate)
        {
            return !_nodes.TryGetValue(relativePath, out Node? node) ? NotFound($"No file exists at '{relativePath}'.")
                : node.Contents is { } contents ? contents
                : NotFound($"'{relativePath}' is a directory, not a file.");
        }
    }

    /// <summary>The nearest proper ancestor of a path that is a file, if any. Call under the lock.</summary>
    /// <param name="relativePath">A normalised relative path.</param>
    /// <returns>The ancestor's path, or <see langword="null"/> if every existing ancestor is a directory.</returns>
    private string? FindFileAncestor(string relativePath)
    {
        for (int slash = relativePath.IndexOf('/'); slash >= 0; slash = relativePath.IndexOf('/', slash + 1))
        {
            string ancestor = relativePath[..slash];
            if (!_nodes.TryGetValue(ancestor, out Node? node))
            {
                return null;
            }

            if (!node.IsDirectory)
            {
                return ancestor;
            }
        }

        return null;
    }

    /// <summary>
    /// Adds a node and links it into its parent, creating missing ancestors. Call under the lock, after
    /// <see cref="FindFileAncestor"/> has found no file on the way.
    /// </summary>
    /// <param name="relativePath">A normalised relative path that doesn't exist yet; not the root.</param>
    /// <param name="node">The new node.</param>
    private void AddNode(string relativePath, Node node)
    {
        int slash = relativePath.LastIndexOf('/');
        string parentPath = slash < 0 ? string.Empty : relativePath[..slash];
        if (!_nodes.TryGetValue(parentPath, out Node? parent))
        {
            parent = Node.CreateDirectory();
            AddNode(parentPath, parent);
        }

        _nodes.Add(relativePath, node);
        parent.Children!.Add(relativePath[(slash + 1)..]);
    }

    /// <summary>A file (its contents) or a directory (its children's names, sorted ordinally).</summary>
    private sealed class Node
    {
        private Node(byte[]? contents, SortedSet<string>? children)
        {
            Contents = contents;
            Children = children;
        }

        /// <summary>A file's bytes: replaced as a whole on every write and never modified, so readers copy it unlocked.</summary>
        public byte[]? Contents { get; set; }

        /// <summary>A directory's child names, or <see langword="null"/> for a file.</summary>
        public SortedSet<string>? Children { get; }

        public bool IsDirectory => Children is not null;

        public static Node CreateFile(byte[] contents) => new(contents, null);

        public static Node CreateDirectory() => new(null, new SortedSet<string>(StringComparer.Ordinal));
    }
}
