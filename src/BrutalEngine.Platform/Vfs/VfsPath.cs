using System;
using System.Runtime.CompilerServices;

namespace BrutalEngine.Platform.Vfs;

/// <summary>
/// A validated, normalised virtual file system path such as <c>res://textures/hero.png</c>: a scheme naming a mount
/// point, then the path inside that mount.
/// </summary>
/// <remarks>
/// <para>
/// The grammar is <c>scheme "://" [ segment *( "/" segment ) ]</c>. A scheme is 1 to 32 characters: a lowercase ASCII
/// letter, then lowercase ASCII letters, digits or <c>-</c>. A segment is one or more Unicode scalar values except
/// U+0000 to U+001F, U+007F and <c>\ / : * ? " &lt; &gt; |</c>; it may not end in <c>.</c> or a space, or be a
/// Windows device name (<c>CON</c>, <c>PRN</c>, <c>AUX</c>, <c>NUL</c>, <c>COM1</c> to <c>COM9</c>, <c>LPT1</c> to
/// <c>LPT9</c>, in any case, alone or before an extension such as <c>nul.txt</c>). A segment is at most 255 bytes in
/// UTF-8 and the normalised path at most 1,024. Each limit is the strictest supported OS's, so a path that is valid
/// on one OS is valid on all of them.
/// </para>
/// <para>
/// Normalisation removes <c>.</c> segments, collapses repeated <c>/</c> and drops a trailing <c>/</c>. Nothing else
/// changes: no case folding, no Unicode normalisation, and <c>\</c> is rejected rather than converted. A <c>..</c>
/// segment is invalid anywhere, so no valid path names anything outside its mount.
/// </para>
/// <para>
/// <c>default(VfsPath)</c> is not a path: <see cref="IsDefault"/> is <see langword="true"/>, APIs that take a path
/// throw <see cref="ArgumentException"/> for it, and its <see cref="Scheme"/>, <see cref="RelativePath"/>,
/// <see cref="Name"/> and <see cref="Combine"/> throw <see cref="InvalidOperationException"/>. It compares equal only
/// to itself, and its <see cref="ToString"/> is empty.
/// </para>
/// <para>
/// Equality is ordinal and case-sensitive over the normalised text, on every OS. Immutable, and safe to use from any
/// thread. <see cref="Parse"/>, <see cref="TryParse"/> and <see cref="Combine"/> allocate the normalised text and its
/// parts (paths are built at load time, not per frame); the other members allocate nothing. See
/// docs/design/platform.md section 5.
/// </para>
/// </remarks>
public readonly struct VfsPath : IEquatable<VfsPath>
{
    private readonly string? _text;
    private readonly string? _scheme;
    private readonly string? _relativePath;
    private readonly string? _name;

    /// <summary>Wraps the parts of a path the parser has validated and normalised.</summary>
    /// <param name="text">The whole normalised path.</param>
    /// <param name="scheme">The scheme, without <c>://</c>.</param>
    /// <param name="relativePath">The path after <c>://</c>; empty for the mount root.</param>
    /// <param name="name">The last segment; empty for the mount root.</param>
    internal VfsPath(string text, string scheme, string relativePath, string name)
    {
        _text = text;
        _scheme = scheme;
        _relativePath = relativePath;
        _name = name;
    }

    /// <summary>The scheme, which names the mount point: <c>res</c> for <c>res://textures/hero.png</c>.</summary>
    /// <value>The scheme, without <c>://</c>. Allocates nothing.</value>
    /// <exception cref="InvalidOperationException">This is <c>default(VfsPath)</c>.</exception>
    public string Scheme => _scheme ?? throw DefaultUsed();

    /// <summary>The path inside the mount: <c>textures/hero.png</c> for <c>res://textures/hero.png</c>.</summary>
    /// <value>
    /// The normalised segments joined by <c>/</c>, with no leading or trailing <c>/</c>; empty for the mount root
    /// (<c>res://</c>). This is the text an <c>IFileSystem</c> receives. Allocates nothing.
    /// </value>
    /// <exception cref="InvalidOperationException">This is <c>default(VfsPath)</c>.</exception>
    public string RelativePath => _relativePath ?? throw DefaultUsed();

    /// <summary>The last segment: <c>hero.png</c> for <c>res://textures/hero.png</c>.</summary>
    /// <value>The last segment, extension included; empty for the mount root. Allocates nothing.</value>
    /// <exception cref="InvalidOperationException">This is <c>default(VfsPath)</c>.</exception>
    public string Name => _name ?? throw DefaultUsed();

    /// <summary>Whether this is <c>default(VfsPath)</c>, which is not a path.</summary>
    /// <value><see langword="true"/> only for the default value; every parsed path is <see langword="false"/>.</value>
    public bool IsDefault => _text is null;

    /// <summary>Parses and normalises a path written in code, such as <c>res://textures/hero.png</c>.</summary>
    /// <remarks>Use <see cref="TryParse"/> for paths from data or user input, where invalid text is expected.</remarks>
    /// <param name="path">The path to parse.</param>
    /// <returns>The normalised path. Allocates its text and parts.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="path"/> breaks a rule of the grammar; the message quotes it and names the rule.
    /// </exception>
    public static VfsPath Parse(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        VfsPathError error = VfsPathParser.Parse(path, out VfsPath result);
        return error == VfsPathError.None
            ? result
            : throw new ArgumentException(VfsPathParser.FormatMessage(error, path), nameof(path));
    }

    /// <summary>Parses and normalises a path from data or user input.</summary>
    /// <param name="path">The text to parse. <see langword="null"/> is rejected.</param>
    /// <param name="result">The normalised path; <c>default</c> when the method returns <see langword="false"/>.</param>
    /// <returns>
    /// <see langword="true"/> if <paramref name="path"/> is a valid path. Allocates the path's text and parts on success,
    /// nothing on failure.
    /// </returns>
    public static bool TryParse(string? path, out VfsPath result)
    {
        if (path is null)
        {
            result = default;
            return false;
        }

        return VfsPathParser.Parse(path, out result) == VfsPathError.None;
    }

    /// <summary>Appends a relative path to this one: <c>res://textures</c> combined with <c>ui/button.png</c>.</summary>
    /// <param name="relative">
    /// The path to append, parsed with the same segment rules and normalised the same way. It must not start with
    /// <c>/</c>, have a scheme or contain a <c>..</c> segment. Empty, or only <c>.</c> segments, returns this path.
    /// </param>
    /// <returns>The combined, normalised path. Allocates its text and parts, unless it returns this path.</returns>
    /// <exception cref="InvalidOperationException">This is <c>default(VfsPath)</c>.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="relative"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="relative"/> breaks a rule, or the combined path is longer than 1,024 bytes in UTF-8; the message
    /// names the rule.
    /// </exception>
    public VfsPath Combine(string relative)
    {
        if (IsDefault)
        {
            throw DefaultUsed();
        }

        ArgumentNullException.ThrowIfNull(relative);
        VfsPathError error = VfsPathParser.Combine(this, relative, out VfsPath result);
        return error == VfsPathError.None
            ? result
            : throw new ArgumentException(VfsPathParser.FormatMessage(error, relative), nameof(relative));
    }

    /// <summary>Whether two paths have the same normalised text, compared ordinally (case-sensitive).</summary>
    /// <param name="other">The path to compare with.</param>
    /// <returns><see langword="true"/> if both are the same path, or both are <c>default</c>. Allocates nothing.</returns>
    public bool Equals(VfsPath other) => string.Equals(_text, other._text, StringComparison.Ordinal);

    /// <summary>Whether <paramref name="obj"/> is a <see cref="VfsPath"/> with the same normalised text.</summary>
    /// <param name="obj">The object to compare with.</param>
    /// <returns><see langword="true"/> if <paramref name="obj"/> is an equal <see cref="VfsPath"/>. Allocates nothing.</returns>
    public override bool Equals(object? obj) => obj is VfsPath other && Equals(other);

    /// <summary>A hash of the normalised text, consistent with <see cref="Equals(VfsPath)"/>.</summary>
    /// <returns>The ordinal hash of the text; <c>0</c> for <c>default</c>. Allocates nothing.</returns>
    public override int GetHashCode() => _text is null ? 0 : StringComparer.Ordinal.GetHashCode(_text);

    /// <summary>The normalised path: <c>res://textures/hero.png</c>.</summary>
    /// <returns>
    /// The path's text, which <see cref="Parse"/> turns back into an equal path; empty for <c>default</c>. Allocates
    /// nothing.
    /// </returns>
    public override string ToString() => _text ?? string.Empty;

    /// <summary>Whether two paths are equal, as <see cref="Equals(VfsPath)"/>.</summary>
    /// <param name="left">The first path.</param>
    /// <param name="right">The second path.</param>
    /// <returns><see langword="true"/> if the paths are equal. Allocates nothing.</returns>
    public static bool operator ==(VfsPath left, VfsPath right) => left.Equals(right);

    /// <summary>Whether two paths differ, as the negation of <see cref="Equals(VfsPath)"/>.</summary>
    /// <param name="left">The first path.</param>
    /// <param name="right">The second path.</param>
    /// <returns><see langword="true"/> if the paths differ. Allocates nothing.</returns>
    public static bool operator !=(VfsPath left, VfsPath right) => !left.Equals(right);

    /// <summary>Throws if an argument is <c>default(VfsPath)</c>: the check every API that takes a path starts with.</summary>
    /// <param name="path">The argument.</param>
    /// <param name="paramName">The argument's name, filled in by the compiler.</param>
    /// <exception cref="ArgumentException"><paramref name="path"/> is <c>default(VfsPath)</c>.</exception>
    internal static void ThrowIfDefault(VfsPath path, [CallerArgumentExpression(nameof(path))] string? paramName = null)
    {
        if (path.IsDefault)
        {
            throw new ArgumentException("default(VfsPath) is not a path: create paths with VfsPath.Parse or VfsPath.TryParse.", paramName);
        }
    }

    private static InvalidOperationException DefaultUsed() =>
        new("This is default(VfsPath), which is not a path: create paths with VfsPath.Parse or VfsPath.TryParse.");
}
