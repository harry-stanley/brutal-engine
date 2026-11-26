namespace BrutalEngine.Platform.Vfs;

/// <summary>Settings for a <see cref="DirectoryFileSystem"/>: which directory it serves, and whether it may change it.</summary>
/// <remarks>
/// An immutable record, safe to share between threads. The constructor of <see cref="DirectoryFileSystem"/> checks it
/// once; nothing reads it afterwards, so a <see langword="with"/> copy made later has no effect on an existing file
/// system. Creating one allocates the record and nothing else.
/// </remarks>
public sealed record DirectoryFileSystemOptions
{
    /// <summary>The directory the file system serves: every relative path is joined under it.</summary>
    /// <value>
    /// A fully qualified path to an existing directory, such as <c>/home/user/game/assets</c> or
    /// <c>C:\Games\Hero\assets</c>; a trailing separator and <c>.</c> or <c>..</c> segments are normalised away.
    /// <see cref="DirectoryFileSystem"/>'s constructor throws <see cref="System.ArgumentException"/> for a relative
    /// path and <see cref="System.IO.DirectoryNotFoundException"/> when no directory exists there.
    /// </value>
    public required string RootDirectory { get; init; }

    /// <summary>Whether the file system refuses every write, delete and create.</summary>
    /// <value>
    /// <see langword="true"/> to mount the directory read-only (the <c>res://</c> mount): writes, deletes and creates
    /// fail with <c>ErrorKind.Unsupported</c> and the directory is never changed. <see langword="false"/>, the
    /// default, allows them.
    /// </value>
    public bool IsReadOnly { get; init; }
}
