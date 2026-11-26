using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using BrutalEngine.Platform.Vfs;
using Xunit;

namespace BrutalEngine.Platform.Tests.Vfs;

public sealed class VfsPathTests
{
    // Phrases each rule's exception message must contain, written from docs/design/platform.md section 5.
    private const string SeparatorRule = "a scheme followed by \"://\"";
    private const string SchemeRule = "the scheme must be 1 to 32 characters";
    private const string TraversalRule = "a \"..\" segment is not allowed";
    private const string BackslashRule = "'\\' is not allowed";
    private const string ControlRule = "control characters";
    private const string ReservedRule = "are not allowed in a segment";
    private const string SurrogateRule = "lone UTF-16 surrogate";
    private const string TrailingRule = "may not end in '.' or a space";
    private const string AbsoluteRule = "must not start with '/'";
    private const string SchemeQualifiedRule = "must not have a scheme";

    [Theory]
    [InlineData("res://textures/hero.png", "res://textures/hero.png", "res", "textures/hero.png", "hero.png")]
    [InlineData("res://textures//hero.png", "res://textures/hero.png", "res", "textures/hero.png", "hero.png")]
    [InlineData("res:///textures", "res://textures", "res", "textures", "textures")]
    [InlineData("res://./textures/./hero.png", "res://textures/hero.png", "res", "textures/hero.png", "hero.png")]
    [InlineData("res://textures/hero.png/", "res://textures/hero.png", "res", "textures/hero.png", "hero.png")]
    [InlineData("res://a/./", "res://a", "res", "a", "a")]
    [InlineData("res://a//.//b///", "res://a/b", "res", "a/b", "b")]
    [InlineData("res://Textures/Hero.PNG", "res://Textures/Hero.PNG", "res", "Textures/Hero.PNG", "Hero.PNG")]
    [InlineData("res://.hidden/a..b/.x.y", "res://.hidden/a..b/.x.y", "res", ".hidden/a..b/.x.y", ".x.y")]
    [InlineData("res://a b/ c", "res://a b/ c", "res", "a b/ c", " c")]
    [InlineData("res://日本/ファイル.png", "res://日本/ファイル.png", "res", "日本/ファイル.png", "ファイル.png")]
    [InlineData("res://\U0001F600", "res://\U0001F600", "res", "\U0001F600", "\U0001F600")]
    [InlineData("res://\u0080\u009F\uFFFD", "res://\u0080\u009F\uFFFD", "res", "\u0080\u009F\uFFFD", "\u0080\u009F\uFFFD")]
    [InlineData("res://name with !#$%&'()+,;=@[]^`{}~", "res://name with !#$%&'()+,;=@[]^`{}~", "res", "name with !#$%&'()+,;=@[]^`{}~", "name with !#$%&'()+,;=@[]^`{}~")]
    [InlineData("r://a", "r://a", "r", "a", "a")]
    [InlineData("test-assets://x", "test-assets://x", "test-assets", "x", "x")]
    [InlineData("a1-://x", "a1-://x", "a1-", "x", "x")]
    [InlineData("abcdefghijklmnopqrstuvwxyz012345://x", "abcdefghijklmnopqrstuvwxyz012345://x", "abcdefghijklmnopqrstuvwxyz012345", "x", "x")]
    public void Parse_ValidPath_Normalises(string input, string expected, string scheme, string relativePath, string name)
    {
        var path = VfsPath.Parse(input);

        Assert.False(path.IsDefault);
        Assert.Equal(expected, path.ToString());
        Assert.Equal(scheme, path.Scheme);
        Assert.Equal(relativePath, path.RelativePath);
        Assert.Equal(name, path.Name);
        Assert.True(VfsPath.TryParse(input, out VfsPath parsed));
        Assert.Equal(path, parsed);
        Assert.Equal(path, VfsPath.Parse(path.ToString()));
    }

    [Theory]
    [InlineData("res://")]
    [InlineData("res:///")]
    [InlineData("res:////")]
    [InlineData("res://.")]
    [InlineData("res://./")]
    [InlineData("res://.//.")]
    public void Parse_MountRoot_HasEmptyRelativePathAndName(string input)
    {
        var path = VfsPath.Parse(input);

        Assert.Equal("res://", path.ToString());
        Assert.Equal("res", path.Scheme);
        Assert.Equal(string.Empty, path.RelativePath);
        Assert.Equal(string.Empty, path.Name);
    }

    public static TheoryData<string, string> InvalidPaths()
    {
        TheoryData<string, string> data = new()
        {
            { "", SeparatorRule },
            { "res", SeparatorRule },
            { "res:", SeparatorRule },
            { "res:/a", SeparatorRule },
            { "res:a", SeparatorRule },
            { "res//a", SeparatorRule },
            { "://a", SchemeRule },
            { "Res://a", SchemeRule },
            { "RES://a", SchemeRule },
            { "1res://a", SchemeRule },
            { "-res://a", SchemeRule },
            { "re_s://a", SchemeRule },
            { "re.s://a", SchemeRule },
            { "re s://a", SchemeRule },
            { "re\\s://a", SchemeRule },
            { " res://a", SchemeRule },
            { "rés://a", SchemeRule },
            { "abcdefghijklmnopqrstuvwxyz0123456://x", SchemeRule },
            { "res://..", TraversalRule },
            { "res://../a", TraversalRule },
            { "res://a/../b", TraversalRule },
            { "res://a/..", TraversalRule },
            { "res://a/../", TraversalRule },
            { "res://a\\b", BackslashRule },
            { "res://\\", BackslashRule },
            { "res://a\\..\\b", BackslashRule },
            { "res://a{0000}b", ControlRule },
            { "res://a{0009}", ControlRule },
            { "res://{000A}", ControlRule },
            { "res://a{001F}b", ControlRule },
            { "res://a{007F}b", ControlRule },
            { "res://a:b", ReservedRule },
            { "res://a*b", ReservedRule },
            { "res://a?b", ReservedRule },
            { "res://a\"b", ReservedRule },
            { "res://a<b", ReservedRule },
            { "res://a>b", ReservedRule },
            { "res://a|b", ReservedRule },
            { "res://a.", TrailingRule },
            { "res://a ", TrailingRule },
            { "res://a./b", TrailingRule },
            { "res://...", TrailingRule },
            { "res:// ", TrailingRule },
            { "res://a/b. ", TrailingRule },
            { "res://{D800}", SurrogateRule },
            { "res://{DFFF}", SurrogateRule },
            { "res://a{D83D}b", SurrogateRule },
            { "res://a{DE00}b", SurrogateRule },
            { "res://a{D83D}", SurrogateRule },
            { "res://{DE00}{D83D}", SurrogateRule },
        };
        return data;
    }

    [Theory]
    [MemberData(nameof(InvalidPaths))]
    public void TryParse_InvalidPath_ReturnsFalse(string input, string rule)
    {
        _ = rule;

        Assert.False(VfsPath.TryParse(Decode(input), out VfsPath result));
        Assert.True(result.IsDefault);
    }

    [Fact]
    public void TryParse_Null_ReturnsFalse()
    {
        Assert.False(VfsPath.TryParse(null, out VfsPath result));
        Assert.True(result.IsDefault);
    }

    [Theory]
    [MemberData(nameof(InvalidPaths))]
    public void Parse_InvalidPath_ThrowsNamingTheRule(string input, string rule)
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(() => VfsPath.Parse(Decode(input)));

        Assert.Equal("path", exception.ParamName);
        Assert.Contains(rule, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_Null_ThrowsArgumentNull()
    {
        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(() => VfsPath.Parse(null!));

        Assert.Equal("path", exception.ParamName);
    }

    [Fact]
    public void Parse_ControlCharacters_AreEscapedInMessage()
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(() => VfsPath.Parse("res://a\nb\uD800"));

        Assert.Contains("'res://a\\u000Ab\\uD800'", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', exception.Message);
    }

    [Fact]
    public void Parse_SurrogatePair_IsQuotedUnescaped()
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(() => VfsPath.Parse("res://\U0001F600*"));

        Assert.Contains("'res://\U0001F600*'", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(256, false)]
    [InlineData(257, true)]
    [InlineData(300, true)]
    public void Parse_LongInput_QuotesAtMost256Characters(int length, bool cut)
    {
        // Short segments, so that no other rule applies, then a reserved character, so that the path is invalid.
        string input = "res://" + Segments(30)[..(length - 7)] + "*";

        ArgumentException exception = Assert.Throws<ArgumentException>(() => VfsPath.Parse(input));

        Assert.Equal(length, input.Length);
        string quoted = cut ? input[..256] + "..." : input;
        Assert.Contains("'" + quoted + "'", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(cut ? input[..257] : input + "...", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_SurrogatePairCutAtTheQuoteLimit_EscapesTheHighHalf()
    {
        // Characters 0 to 254 are ASCII and 255 and 256 are the halves of U+1F600, so the 256 quoted characters end in
        // a high surrogate without its low half. The message escapes it rather than carry invalid UTF-16.
        string input = "res://" + Segments(25)[..249] + "\U0001F600/b*";

        ArgumentException exception = Assert.Throws<ArgumentException>(() => VfsPath.Parse(input));

        Assert.True(char.IsHighSurrogate(input[255]) && char.IsLowSurrogate(input[256]));
        Assert.Contains("'" + input[..255] + "\\uD83D...'", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain('\uD83D', exception.Message);
        Assert.DoesNotContain('\uDE00', exception.Message);
    }

    [Theory]
    [InlineData("res://", "a", "res://a")]
    [InlineData("res://", "a/b", "res://a/b")]
    [InlineData("res://textures", "hero.png", "res://textures/hero.png")]
    [InlineData("res://textures", "./ui//button.png/", "res://textures/ui/button.png")]
    [InlineData("res://textures", "", "res://textures")]
    [InlineData("res://textures", ".", "res://textures")]
    [InlineData("user://saves", "Slot 1.dat", "user://saves/Slot 1.dat")]
    public void Combine_RelativePath_AppendsNormalisedSegments(string basePath, string relative, string expected)
    {
        VfsPath combined = VfsPath.Parse(basePath).Combine(relative);

        Assert.Equal(expected, combined.ToString());
        Assert.Equal(VfsPath.Parse(expected), combined);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../a")]
    [InlineData("a/..")]
    [InlineData("a/../b")]
    [InlineData("./..")]
    public void Combine_Traversal_Throws(string relative)
    {
        var path = VfsPath.Parse("res://textures/ui");

        ArgumentException exception = Assert.Throws<ArgumentException>(() => path.Combine(relative));

        Assert.Equal("relative", exception.ParamName);
        Assert.Contains(TraversalRule, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/a", AbsoluteRule)]
    [InlineData("/", AbsoluteRule)]
    [InlineData("res://a", SchemeQualifiedRule)]
    [InlineData("a/user://b", SchemeQualifiedRule)]
    [InlineData("c:a", ReservedRule)]
    [InlineData("a\\b", BackslashRule)]
    [InlineData("a.", TrailingRule)]
    [InlineData("{D800}", SurrogateRule)]
    public void Combine_InvalidRelativePath_ThrowsNamingTheRule(string relative, string rule)
    {
        var path = VfsPath.Parse("res://a");

        ArgumentException exception = Assert.Throws<ArgumentException>(() => path.Combine(Decode(relative)));

        Assert.Equal("relative", exception.ParamName);
        Assert.Contains(rule, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Combine_NullRelative_Throws()
    {
        var path = VfsPath.Parse("res://a");

        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(() => path.Combine(null!));

        Assert.Equal("relative", exception.ParamName);
    }

    [Fact]
    public void Equality_IsOrdinalCaseSensitive()
    {
        var lower = VfsPath.Parse("res://textures/hero.png");
        var upper = VfsPath.Parse("res://Textures/hero.png");
        var sameAfterNormalising = VfsPath.Parse("res://textures//./hero.png/");
        var otherScheme = VfsPath.Parse("user://textures/hero.png");
        var decomposed = VfsPath.Parse("res://e\u0301");
        var composed = VfsPath.Parse("res://\u00E9");

        Assert.NotEqual(lower, upper);
        Assert.True(lower != upper);
        Assert.False(lower == upper);
        Assert.NotEqual(lower, otherScheme);
        Assert.NotEqual(decomposed, composed);
        Assert.Equal(lower, sameAfterNormalising);
        Assert.True(lower == sameAfterNormalising);
        Assert.False(lower != sameAfterNormalising);
        Assert.True(lower.Equals((object)sameAfterNormalising));
        Assert.False(lower.Equals((object)"res://textures/hero.png"));
        Assert.False(lower.Equals(null));
        Assert.Equal(lower.GetHashCode(), sameAfterNormalising.GetHashCode());
    }

    [Fact]
    public void Default_UsedAsArgument_Throws()
    {
        VfsPath target = default;

        ArgumentException exception = Assert.Throws<ArgumentException>(() => VfsPath.ThrowIfDefault(target));

        Assert.Equal("target", exception.ParamName);
        Assert.Contains("default(VfsPath)", exception.Message, StringComparison.Ordinal);
        VfsPath.ThrowIfDefault(VfsPath.Parse("res://"));
    }

    [Fact]
    public void Default_Members_Throw()
    {
        VfsPath path = default;

        Assert.True(path.IsDefault);
        Assert.Throws<InvalidOperationException>(() => path.Scheme);
        Assert.Throws<InvalidOperationException>(() => path.RelativePath);
        Assert.Throws<InvalidOperationException>(() => path.Name);
        Assert.Throws<InvalidOperationException>(() => path.Combine("a"));
    }

    [Fact]
    public void Default_Comparison_EqualsOnlyDefault()
    {
        VfsPath path = default;
        var root = VfsPath.Parse("res://");

        Assert.Equal(default, path);
        Assert.True(path == default);
        Assert.NotEqual(root, path);
        Assert.True(root != path);
        Assert.Equal(0, path.GetHashCode());
        Assert.Equal(string.Empty, path.ToString());
    }

    [Fact]
    public void FailedTryParse_GettersAndEquality_AllocateNothing()
    {
        // One input per rule a parse checks, each rejected before anything is built.
        string[] invalid = ["textures/hero.png", "Res://a", "res://a/../b", "res://a\\b", "res://a\nb", "res://a*b", "res://a\uD800", "res://a."];
        var path = VfsPath.Parse("res://textures/hero.png");

        // Equal but parsed from other text, so equality compares two string instances.
        var same = VfsPath.Parse("res://textures//hero.png");
        object boxed = same;
        long checksum = Exercise(invalid, path, same, boxed, 1);

        // A collection between warm-up and the window rebuilds any cache that only a weak handle keeps.
        GC.Collect();
        long before = GC.GetAllocatedBytesForCurrentThread();
        checksum += Exercise(invalid, path, same, boxed, 100);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.Equal(101 * 54, checksum);
    }

    // The given number of copies of "abcdefghi/": valid segments, none near a length limit.
    private static string Segments(int count) => new StringBuilder().Insert(0, "abcdefghi/", count).ToString();

    // Per round: 0 for the rejected inputs, 51 for the lengths of the parts and the text, 3 for the comparisons.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long Exercise(string[] invalid, VfsPath path, VfsPath same, object boxed, int rounds)
    {
        long sum = 0;
        for (int round = 0; round < rounds; round++)
        {
            foreach (string input in invalid)
            {
                sum += VfsPath.TryParse(input, out VfsPath result) || !result.IsDefault ? 1000 : 0;
            }

            sum += path.Scheme.Length + path.RelativePath.Length + path.Name.Length + path.ToString().Length;
            sum += path.Equals(same) && path == same && !(path != same) && path.Equals(boxed) ? 1 : 0;
            sum += path.GetHashCode() == same.GetHashCode() ? 1 : 0;
            sum += path.Combine(".") == path ? 1 : 0;
        }

        return sum;
    }

    // Replaces each "{XXXX}" with the UTF-16 code unit U+XXXX, so test data stays printable in test names.
    private static string Decode(string encoded)
    {
        StringBuilder decoded = new(encoded.Length);
        for (int i = 0; i < encoded.Length; i++)
        {
            if (encoded[i] == '{' && i + 5 < encoded.Length && encoded[i + 5] == '}')
            {
                decoded.Append((char)int.Parse(encoded.AsSpan(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                i += 5;
            }
            else
            {
                decoded.Append(encoded[i]);
            }
        }

        return decoded.ToString();
    }
}
