using System;
using System.Text;
using System.Threading;
using BrutalEngine.Platform.Vfs;
using CsCheck;
using Xunit;

namespace BrutalEngine.Platform.Tests.Vfs;

public sealed class VfsPathPropertyTests
{
    private const long Iterations = 20_000;

    // Allowed characters by UTF-8 width. None is '.', a space or a letter of a device name, so any non-empty mix of
    // them is a valid segment and only its length decides.
    private const string OneByte = "bdefghijkqswyz0123456789_-~";
    private const int TwoByteFirst = 0x0100;  // Latin Extended-A, U+0100-U+017F
    private const int ThreeByteFirst = 0x4E00; // CJK ideographs, U+4E00-U+9FFF
    private const int FourByteFirst = 0x1F300; // pictographs, U+1F300-U+1F5FF (surrogate pairs)

    // Path-shaped text: a scheme, then segments that are often empty, ".", "..", or break one rule.
    private static readonly Gen<string> _genPathText = Gen.Select(
        OneOf("res", "user", "a-1", "Res", "", "res:"),
        OneOf(
                "", "", ".", "..", "a", "Hero.png", ".hidden", "a..b", "日本", "\U0001F600", " x", "x.", "x ", "a\\b",
                "a:b", "*", "CON", "nul.txt", "COM10", "\u0001", "\u007F", "\uD800", "\uDC00x", "e\u0301")
            .Array[0, 12],
        Gen.Bool,
        (scheme, segments, trailingSlash) => scheme + "://" + string.Join('/', segments) + (trailingSlash ? "/" : ""));

    // Arbitrary text over the characters every rule looks at, usually behind a valid scheme.
    private static readonly Gen<string> _genHostileText = Gen.Select(
        Gen.Frequency((4, Gen.Const("res://")), (1, Gen.Const(""))),
        Gen.Char["/\\.:*?\"<>| a\u0000\t\u007F\u00E9\u65E5\uD83D\uDE00"].Array[0, 40],
        (prefix, chars) => prefix + new string(chars));

    [Fact]
    public void Normalise_IsIdempotent()
    {
        int parsed = 0;
        _genPathText.Sample(text =>
        {
            if (!VfsPath.TryParse(text, out VfsPath path))
            {
                return;
            }

            Interlocked.Increment(ref parsed);
            string normalised = path.ToString();

            Assert.True(VfsPath.TryParse(normalised, out VfsPath again), normalised);
            Assert.Equal(path, again);
            Assert.Equal(normalised, again.ToString());
            Assert.Equal(path.Scheme + "://" + path.RelativePath, normalised);
            Assert.Equal(path.RelativePath[(path.RelativePath.LastIndexOf('/') + 1)..], path.Name);
            Assert.Equal(path, path.Combine(string.Empty));
        }, iter: Iterations);

        Assert.True(parsed > Iterations / 100, $"Only {parsed} of {Iterations} samples parsed.");
    }

    [Fact]
    public void ParsedPath_HasNoTraversalOrBackslash()
    {
        int parsed = 0;
        var samples = Gen.Select(
            Gen.Frequency((1, _genPathText), (1, _genHostileText)),
            Gen.Frequency((1, _genPathText.Select(t => t.Replace("res://", "", StringComparison.Ordinal))), (1, _genHostileText)));
        samples.Sample((text, relative) =>
        {
            if (VfsPath.TryParse(text, out VfsPath path))
            {
                Interlocked.Increment(ref parsed);
                AssertNormalised(path);

                VfsPath combined;
                try
                {
                    combined = path.Combine(relative);
                }
                catch (ArgumentException)
                {
                    return;
                }

                AssertNormalised(combined);
                Assert.StartsWith(path.ToString(), combined.ToString(), StringComparison.Ordinal);
            }
        }, iter: Iterations);

        Assert.True(parsed > Iterations / 100, $"Only {parsed} of {Iterations} samples parsed.");
    }

    [Fact]
    public void SegmentByteLimit_IsExact()
    {
        // Byte targets cluster at the 255-byte limit; the units mix every UTF-8 width, so a limit counted in UTF-16
        // code units or in characters accepts segments that are too long.
        var segments = Gen.Select(
            Gen.Select(Gen.Int[1, 4], Gen.Int[0, 1023]).Array[0, 300],
            Gen.Frequency((3, Gen.Int[250, 260]), (1, Gen.Int[1, 700])),
            (units, target) => BuildSegment(units, target));

        segments.Sample(segment =>
        {
            int bytes = Encoding.UTF8.GetByteCount(segment);

            Assert.Equal(bytes <= 255, VfsPath.TryParse("res://" + segment, out _));
            Assert.Equal(bytes <= 255, VfsPath.TryParse("res://dir/" + segment + "/file", out _));
        }, iter: Iterations);
    }

    [Fact]
    public void PathByteLimit_IsExact()
    {
        // Five or six segments of at most 255 bytes each; totals cluster at the 1,024-byte limit.
        var paths = Gen.Select(
            Gen.Select(Gen.Int[1, 4], Gen.Int[0, 1023]).Array[0, 300],
            Gen.Int[5, 6],
            Gen.Frequency((3, Gen.Int[1018, 1030]), (1, Gen.Int[25, 1285])),
            (units, count, total) => BuildPath(units, count, total));

        paths.Sample(path =>
        {
            int bytes = Encoding.UTF8.GetByteCount(path);

            Assert.Equal(bytes <= 1024, VfsPath.TryParse(path, out _));
        }, iter: Iterations);
    }

    private static Gen<string> OneOf(params string[] values) => Gen.Int[0, values.Length - 1].Select(i => values[i]);

    private static void AssertNormalised(VfsPath path)
    {
        string text = path.ToString();
        string relative = path.RelativePath;

        Assert.DoesNotContain('\\', text);
        Assert.DoesNotContain("//", relative, StringComparison.Ordinal);
        Assert.False(relative.StartsWith('/'), text);
        Assert.False(relative.EndsWith('/'), text);
        if (relative.Length > 0)
        {
            foreach (string segment in relative.Split('/'))
            {
                Assert.NotEqual("..", segment);
                Assert.NotEqual(".", segment);
                Assert.NotEqual(string.Empty, segment);
                Assert.True(Encoding.UTF8.GetByteCount(segment) <= 255, text);
            }
        }

        Assert.True(Encoding.UTF8.GetByteCount(text) <= 1024);
    }

    // A segment of exactly `target` UTF-8 bytes: the units that fit, in order, then one-byte characters to fill.
    private static string BuildSegment((int Width, int Pick)[] units, int target)
    {
        StringBuilder segment = new();
        int bytes = 0;
        foreach ((int width, int pick) in units)
        {
            if (bytes + width > target)
            {
                continue;
            }

            segment.Append(width switch
            {
                1 => OneByte[pick % OneByte.Length].ToString(),
                2 => ((char)(TwoByteFirst + (pick % 0x80))).ToString(),
                3 => ((char)(ThreeByteFirst + pick)).ToString(),
                _ => char.ConvertFromUtf32(FourByteFirst + (pick % 0x300)),
            });
            bytes += width;
        }

        for (; bytes < target; bytes++)
        {
            segment.Append(OneByte[bytes % OneByte.Length]);
        }

        return segment.ToString();
    }

    // "res://" and `count` segments joined by '/', exactly `total` UTF-8 bytes, each segment within 255 bytes.
    private static string BuildPath((int Width, int Pick)[] units, int count, int total)
    {
        int content = total - "res://".Length - (count - 1);
        StringBuilder path = new("res://");
        for (int i = 0; i < count; i++)
        {
            if (i > 0)
            {
                path.Append('/');
            }

            int target = (content / count) + (i < content % count ? 1 : 0);
            path.Append(BuildSegment(units[(i * units.Length / count)..], target));
        }

        return path.ToString();
    }
}
