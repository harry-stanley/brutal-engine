using System;
using System.Globalization;

namespace BrutalEngine.Platform.Vfs;

/// <summary>One child of a directory, as a file system's <c>List</c> returns it: its name, kind and length.</summary>
/// <param name="Name">The child's name: one path segment, without a <c>/</c>. Must not be empty.</param>
/// <param name="Kind">The child's kind: <see cref="VfsEntryKind.File"/> or <see cref="VfsEntryKind.Directory"/>.</param>
/// <param name="Length">The file's length in bytes, at least 0; always 0 for a directory.</param>
/// <remarks>
/// <para>
/// The constructor checks the values: a <see langword="null"/> name throws <see cref="ArgumentNullException"/>, an
/// empty name <see cref="ArgumentException"/>, a kind other than <see cref="VfsEntryKind.File"/> or
/// <see cref="VfsEntryKind.Directory"/> <see cref="ArgumentOutOfRangeException"/>, a negative length
/// <see cref="ArgumentOutOfRangeException"/>, and a directory with a length other than 0
/// <see cref="ArgumentException"/>. Each exception names the parameter. The properties have no <c>init</c>
/// accessors, so a <c>with</c> expression can't bypass the checks. The name is not otherwise checked: a file system
/// reports the names it holds.
/// </para>
/// <para>
/// <c>default(VfsEntry)</c> is not an entry any file system returns: its <see cref="Name"/> is empty, its
/// <see cref="Kind"/> <see cref="VfsEntryKind.None"/> and its <see cref="Length"/> 0.
/// </para>
/// <para>
/// Equality compares every property, names ordinally (case-sensitive, no Unicode normalisation). Immutable and safe
/// to use from any thread. No member allocates, except <see cref="ToString"/>, which formats with the invariant
/// culture. See docs/design/platform.md section 5, "Mounts and operations".
/// </para>
/// </remarks>
public readonly record struct VfsEntry(string Name, VfsEntryKind Kind, long Length)
{
    private readonly string? _name = CheckName(Name, nameof(Name));

    /// <summary>The child's name: one path segment, extension included.</summary>
    /// <value>The name the entry was created with; empty only for <c>default(VfsEntry)</c>. Allocates nothing.</value>
    public string Name => _name ?? string.Empty;

    /// <summary>Whether the child is a file or a directory.</summary>
    /// <value>
    /// <see cref="VfsEntryKind.File"/> or <see cref="VfsEntryKind.Directory"/>; <see cref="VfsEntryKind.None"/> only
    /// for <c>default(VfsEntry)</c>. Allocates nothing.
    /// </value>
    public VfsEntryKind Kind { get; } = CheckKind(Kind, nameof(Kind));

    /// <summary>The file's length in bytes when the directory was listed.</summary>
    /// <value>At least 0; always 0 for a directory. Allocates nothing.</value>
    public long Length { get; } = CheckLength(Length, Kind, nameof(Length));

    /// <summary>
    /// Returns every property in the record's usual form, for example
    /// <c>VfsEntry { Name = hero.png, Kind = File, Length = 1024 }</c>.
    /// </summary>
    /// <returns>A diagnostic string, formatted with the invariant culture whatever the current culture is. Allocates.</returns>
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"VfsEntry {{ Name = {Name}, Kind = {Kind}, Length = {Length} }}");

    private static string CheckName(string name, string paramName)
    {
        ArgumentNullException.ThrowIfNull(name, paramName);
        return name.Length != 0
            ? name
            : throw new ArgumentException("An entry's name must not be empty.", paramName);
    }

    private static VfsEntryKind CheckKind(VfsEntryKind kind, string paramName) =>
        kind is VfsEntryKind.File or VfsEntryKind.Directory
            ? kind
            : throw new ArgumentOutOfRangeException(paramName, kind, "An entry is a File or a Directory.");

    private static long CheckLength(long length, VfsEntryKind kind, string paramName)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length, paramName);
        return kind != VfsEntryKind.Directory || length == 0
            ? length
            : throw new ArgumentException("A directory entry's length must be 0.", paramName);
    }
}
