using System;
using System.Buffers;
using System.Globalization;
using System.Text;

namespace BrutalEngine.Platform.Vfs;

/// <summary>
/// Validates and normalises virtual file system paths (docs/design/platform.md section 5, "Paths"). Pure and
/// stateless, so safe on any thread. Validation allocates nothing; a valid path allocates its normalised text and parts.
/// </summary>
internal static class VfsPathParser
{
    /// <summary>The longest scheme, in characters (all ASCII).</summary>
    public const int MaxSchemeLength = 32;

    /// <summary>The scheme separator.</summary>
    public const string SchemeSeparator = "://";

    // The longest piece of an invalid input quoted in an exception message.
    private const int MaxQuotedChars = 256;

    private static readonly SearchValues<char> _reservedCharacters = SearchValues.Create(":*?\"<>|");

    /// <summary>Parses and normalises a full path such as <c>res://textures/hero.png</c>.</summary>
    /// <param name="path">The text to parse.</param>
    /// <param name="result">The normalised path; <c>default</c> unless the method returns <see cref="VfsPathError.None"/>.</param>
    /// <returns>The first rule <paramref name="path"/> breaks, or <see cref="VfsPathError.None"/>.</returns>
    public static VfsPathError Parse(string path, out VfsPath result)
    {
        result = default;
        int colon = path.IndexOf(':', StringComparison.Ordinal);
        if (colon < 0 || !path.AsSpan(colon).StartsWith(SchemeSeparator, StringComparison.Ordinal))
        {
            return VfsPathError.MissingSchemeSeparator;
        }

        if (!IsValidScheme(path.AsSpan(0, colon)))
        {
            return VfsPathError.InvalidScheme;
        }

        int relativeStart = colon + SchemeSeparator.Length;
        ReadOnlySpan<char> relative = path.AsSpan(relativeStart);
        VfsPathError error = Measure(relative, out int keptChars);
        if (error != VfsPathError.None)
        {
            return error;
        }

        // Normalising only removes characters, so an unchanged length means the input is already normalised.
        string text = keptChars == relative.Length
            ? path
            : string.Create(relativeStart + keptChars, (path, relativeStart), static (destination, state) =>
            {
                state.path.AsSpan(0, state.relativeStart).CopyTo(destination);
                WriteSegments(state.path.AsSpan(state.relativeStart), destination[state.relativeStart..], leadingSlash: false);
            });
        result = Create(text, colon);
        return VfsPathError.None;
    }

    /// <summary>Appends a relative path, parsed with the same segment rules, to a valid path.</summary>
    /// <param name="basePath">The path to extend. Not <c>default</c>.</param>
    /// <param name="relative">The relative path, such as <c>ui/button.png</c>.</param>
    /// <param name="result">The combined, normalised path; <c>default</c> unless the method returns <see cref="VfsPathError.None"/>.</param>
    /// <returns>The first rule the relative path breaks, or <see cref="VfsPathError.None"/>.</returns>
    public static VfsPathError Combine(VfsPath basePath, string relative, out VfsPath result)
    {
        result = default;
        if (relative.StartsWith('/'))
        {
            return VfsPathError.AbsoluteRelativePath;
        }

        if (relative.Contains(SchemeSeparator, StringComparison.Ordinal))
        {
            return VfsPathError.SchemeQualifiedRelativePath;
        }

        VfsPathError error = Measure(relative, out int keptChars);
        if (error != VfsPathError.None)
        {
            return error;
        }

        if (keptChars == 0)
        {
            result = basePath;
            return VfsPathError.None;
        }

        string baseText = basePath.ToString();
        bool joiner = basePath.RelativePath.Length > 0;
        int separatorLength = joiner ? 1 : 0;

        string text = string.Create(baseText.Length + separatorLength + keptChars, (baseText, relative, joiner), static (destination, state) =>
        {
            state.baseText.AsSpan().CopyTo(destination);
            WriteSegments(state.relative, destination[state.baseText.Length..], state.joiner);
        });
        result = Create(text, basePath.Scheme.Length);
        return VfsPathError.None;
    }

    /// <summary>Formats the exception message for a rule that <paramref name="input"/> breaks.</summary>
    /// <param name="error">The rule. Not <see cref="VfsPathError.None"/>.</param>
    /// <param name="input">The rejected text, quoted with control characters and lone surrogates escaped.</param>
    /// <returns>The message. Allocates.</returns>
    public static string FormatMessage(VfsPathError error, string input)
    {
        string rule = error switch
        {
            VfsPathError.MissingSchemeSeparator => "it must start with a scheme followed by \"://\", as in \"res://textures/hero.png\"",
            VfsPathError.InvalidScheme => "the scheme must be 1 to 32 characters: a lowercase ASCII letter, then lowercase ASCII letters, digits or '-'",
            VfsPathError.Traversal => "a \"..\" segment is not allowed: a path never names anything outside its mount",
            VfsPathError.Backslash => "'\\' is not allowed: separate segments with '/'",
            VfsPathError.ControlCharacter => "control characters (U+0000 to U+001F and U+007F) are not allowed",
            VfsPathError.ReservedCharacter => "the characters : * ? \" < > | are not allowed in a segment",
            VfsPathError.LoneSurrogate => "a lone UTF-16 surrogate is not a Unicode scalar value",
            VfsPathError.TrailingDotOrSpace => "a segment may not end in '.' or a space",
            VfsPathError.AbsoluteRelativePath => "a relative path must not start with '/'",
            VfsPathError.SchemeQualifiedRelativePath => "a relative path must not have a scheme",
            _ => throw new ArgumentOutOfRangeException(nameof(error), error, "Not a path rule."),
        };
        return $"'{Quote(input)}' is not a valid VFS path: {rule}.";
    }

    // lower *( lower / digit / "-" ), 1 to 32 characters.
    private static bool IsValidScheme(ReadOnlySpan<char> scheme)
    {
        if (scheme.IsEmpty || scheme.Length > MaxSchemeLength || !char.IsAsciiLetterLower(scheme[0]))
        {
            return false;
        }

        foreach (char c in scheme)
        {
            if (!char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c) && c != '-')
            {
                return false;
            }
        }

        return true;
    }

    // Validates every segment, and measures what normalisation keeps: the segments other than empty and "." ones,
    // joined by single '/'.
    private static VfsPathError Measure(ReadOnlySpan<char> relative, out int keptChars)
    {
        keptChars = 0;
        while (true)
        {
            int slash = relative.IndexOf('/');
            ReadOnlySpan<char> segment = slash < 0 ? relative : relative[..slash];
            if (!IsDropped(segment))
            {
                VfsPathError error = ValidateSegment(segment);
                if (error != VfsPathError.None)
                {
                    return error;
                }

                int separator = keptChars > 0 ? 1 : 0;
                keptChars += separator + segment.Length;
            }

            if (slash < 0)
            {
                return VfsPathError.None;
            }

            relative = relative[(slash + 1)..];
        }
    }

    // Empty segments (repeated, leading or trailing '/') and "." are removed by normalisation.
    private static bool IsDropped(ReadOnlySpan<char> segment) => segment.IsEmpty || segment is ".";

    private static VfsPathError ValidateSegment(ReadOnlySpan<char> segment)
    {
        if (segment is "..")
        {
            return VfsPathError.Traversal;
        }

        int i = 0;
        while (i < segment.Length)
        {
            char c = segment[i];
            if (c < '\u0020' || c == '\u007F')
            {
                return VfsPathError.ControlCharacter;
            }

            if (c == '\\')
            {
                return VfsPathError.Backslash;
            }

            if (_reservedCharacters.Contains(c))
            {
                return VfsPathError.ReservedCharacter;
            }

            // Done only for a scalar value: a lone surrogate is InvalidData, a trailing high surrogate NeedMoreData.
            if (Rune.DecodeFromUtf16(segment[i..], out _, out int consumed) != OperationStatus.Done)
            {
                return VfsPathError.LoneSurrogate;
            }

            i += consumed;
        }

        return segment[^1] is '.' or ' ' ? VfsPathError.TrailingDotOrSpace : VfsPathError.None;
    }

    // Writes the kept segments of an already validated relative path, joined by '/', with a '/' before the first
    // one when leadingSlash is set. The destination is exactly as long as Measure said.
    private static void WriteSegments(ReadOnlySpan<char> relative, Span<char> destination, bool leadingSlash)
    {
        int written = 0;
        bool separate = leadingSlash;
        while (true)
        {
            int slash = relative.IndexOf('/');
            ReadOnlySpan<char> segment = slash < 0 ? relative : relative[..slash];
            if (!IsDropped(segment))
            {
                if (separate)
                {
                    destination[written++] = '/';
                }

                segment.CopyTo(destination[written..]);
                written += segment.Length;
                separate = true;
            }

            if (slash < 0)
            {
                return;
            }

            relative = relative[(slash + 1)..];
        }
    }

    // Splits a normalised path's text into its parts: the scheme ends at schemeLength, the name after the last '/'
    // of the relative path.
    private static VfsPath Create(string text, int schemeLength)
    {
        int relativeStart = schemeLength + SchemeSeparator.Length;
        string relativePath = text[relativeStart..];
        int lastSlash = relativePath.LastIndexOf('/');
        string name = lastSlash < 0 ? relativePath : relativePath[(lastSlash + 1)..];
        return new VfsPath(text, text[..schemeLength], relativePath, name);
    }

    // The input as a message quotes it: at most MaxQuotedChars characters, with control characters and lone
    // surrogates written as \uXXXX so that a message can't forge log lines or carry invalid UTF-16.
    private static string Quote(string input)
    {
        ReadOnlySpan<char> shown = input.AsSpan(0, Math.Min(input.Length, MaxQuotedChars));
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

        if (shown.Length < input.Length)
        {
            quoted.Append("...");
        }

        return quoted.ToString();
    }
}
