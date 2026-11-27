namespace BrutalEngine.Platform.Vfs;

/// <summary>
/// Settings for a <see cref="DirectoryFileSystem"/>: which directory it serves, whether it may change it, and whether
/// paths must match the letter case on disk.
/// </summary>
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

    /// <summary>Whether every segment of a path must be spelled exactly as the name on disk, letter case included.</summary>
    /// <value>
    /// <see langword="true"/>, the default, to check each segment against its parent directory's listing on every OS,
    /// so a game that asks for <c>hero.png</c> when the file is <c>Hero.png</c> fails on a case-insensitive file
    /// system (Windows) as it would on a case-sensitive one (Linux): <see cref="DirectoryFileSystem.GetEntryKind"/>
    /// answers <see cref="VfsEntryKind.None"/> and every other operation fails with <c>ErrorKind.NotFound</c>, naming
    /// the spelling on disk. <see langword="false"/> leaves lookups to the OS, so one spelling may open another.
    /// <see cref="DirectoryFileSystem"/>'s remarks give the rules.
    /// </value>
    public bool VerifyCase { get; init; } = true;
}
