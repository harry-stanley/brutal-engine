namespace BrutalEngine.Platform.Vfs;

/// <summary>What a virtual file system path names: nothing, a file or a directory.</summary>
/// <remarks>
/// The values are fixed: new kinds are appended, never renumbered. <c>default</c> is <see cref="None"/>. See
/// docs/design/platform.md section 5, "Mounts and operations".
/// </remarks>
public enum VfsEntryKind
{
    /// <summary>Nothing is at the path. The default.</summary>
    None = 0,

    /// <summary>A file: a sequence of bytes.</summary>
    File = 1,

    /// <summary>A directory, which holds files and other directories. A mount's root is always one.</summary>
    Directory = 2,
}
