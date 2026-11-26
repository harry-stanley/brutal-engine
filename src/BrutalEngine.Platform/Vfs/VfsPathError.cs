namespace BrutalEngine.Platform.Vfs;

/// <summary>The rule a virtual file system path breaks, or <see cref="None"/> (docs/design/platform.md section 5).</summary>
internal enum VfsPathError
{
    /// <summary>The path is valid.</summary>
    None = 0,

    /// <summary>No <c>scheme://</c> prefix: no <c>:</c>, or the first <c>:</c> isn't followed by <c>//</c>.</summary>
    MissingSchemeSeparator,

    /// <summary>The scheme isn't 1 to 32 characters of <c>lower *( lower / digit / "-" )</c>.</summary>
    InvalidScheme,

    /// <summary>A <c>..</c> segment.</summary>
    Traversal,

    /// <summary>A <c>\</c> in a segment. A <c>\</c> in the scheme is <see cref="InvalidScheme"/>.</summary>
    Backslash,

    /// <summary>A character in U+0000 to U+001F, or U+007F.</summary>
    ControlCharacter,

    /// <summary>One of <c>: * ? " &lt; &gt; |</c> in a segment.</summary>
    ReservedCharacter,

    /// <summary>A UTF-16 surrogate that isn't part of a pair, so not a Unicode scalar value.</summary>
    LoneSurrogate,

    /// <summary>A segment that ends in <c>.</c> or a space.</summary>
    TrailingDotOrSpace,

    /// <summary>A relative path passed to <c>Combine</c> that starts with <c>/</c>.</summary>
    AbsoluteRelativePath,

    /// <summary>A relative path passed to <c>Combine</c> that contains <c>://</c>.</summary>
    SchemeQualifiedRelativePath,
}
