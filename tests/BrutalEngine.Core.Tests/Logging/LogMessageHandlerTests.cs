using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Unicode;
using BrutalEngine.Core.Logging;
using CsCheck;
using Xunit;

namespace BrutalEngine.Core.Tests.Logging;

public sealed class LogMessageHandlerTests
{
    private const string Ellipsis = "\u2026";

    [Fact]
    public void Format_DisabledLevel_DoesNotFormatArguments()
    {
        RecordingSink sink = new();
        using Logger logger = new(new LoggerOptions { Sinks = [sink] });
        LogCategory category = logger.CreateCategory("Core");
        CountingFormattable value = new();

        category.Debug($"{value}");
        category.Trace($"{value} {value}");
        category.Log(LogLevel.Debug, $"{value}");
        Assert.Equal(0, value.FormatCount);

        category.Information($"{value}");
        Assert.Equal(1, value.FormatCount);
        Assert.Equal(["x"], sink.Messages);
    }

    [Fact]
    public void Format_MixedLiteralsAndValues_ProducesExpectedUtf8()
    {
        string? none = null;
        byte[] message = Format(c => c.Information(
            $"i={42} l={-7L} f={1.5f} d={2.25} b={true}/{false} s={"str"} c={"chars".AsSpan()} u={"bytes"u8} ch={'\u00e9'} n={none}|\U0001F600"));

        Assert.Equal(Encoding.UTF8.GetBytes("i=42 l=-7 f=1.5 d=2.25 b=True/False s=str c=chars u=bytes ch=\u00e9 n=|\U0001F600"), message);
    }

    [Fact]
    public void Format_WithFormatString_UsesInvariantCulture()
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
        try
        {
            string message = FormatString(c => c.Information(
                $"{1234.5:N1} {3.14159:F2} {1234.5} {255:X4} {new DateTime(2026, 11, 25, 0, 0, 0, DateTimeKind.Utc):yyyy-MM-dd}"));

            Assert.Equal("1,234.5 3.14 1234.5 00FF 2026-11-25", message);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void Format_OtherTypes_FallBackToInvariantFormatting()
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
        try
        {
            string message = FormatString(c => c.Information(
                $"{new SpanOnly(1.5)} {new FormattableOnly(2.5)} {new PlainObject()} {DayOfWeek.Friday} {(int?)5} {(object?)null}|"));

            Assert.Equal("1.5 2.5 plain Friday 5 |", message);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void Format_InvalidUtf16AndUtf8_AreReplaced()
    {
        byte[] invalidUtf8 = [(byte)'a', 0xFF, (byte)'b', 0xE2, 0x82];
        string loneSurrogate = "x\uD800y";

        // Compare bytes: decoding the message to a string would itself hide invalid UTF-8 behind U+FFFD.
        byte[] message = Format(c => c.Information($"{loneSurrogate} {invalidUtf8.AsSpan()} {new BrokenUtf8()}"));

        Assert.True(Utf8.IsValid(message));
        Assert.Equal(Encoding.UTF8.GetBytes("x\uFFFDy a\uFFFDb\uFFFD \uFFFDok"), message);
    }

    [Fact]
    public void Format_LongerThanMaximum_TruncatesWithEllipsis()
    {
        CountingFormattable after = new();
        string ascii = new('a', 100);
        string twoByte = new('\u00e9', 40);
        string emoji = string.Concat(Enumerable.Repeat("\U0001F600", 20));

        Assert.Equal(new string('a', 61) + Ellipsis, FormatString(c => c.Information($"{ascii}{after}"), 64));
        Assert.Equal(new string('\u00e9', 30) + Ellipsis, FormatString(c => c.Information($"{twoByte}"), 64));
        Assert.Equal(string.Concat(Enumerable.Repeat("\U0001F600", 15)) + Ellipsis, FormatString(c => c.Information($"{emoji}"), 64));
        Assert.Equal(new string('7', 60) + "1" + Ellipsis, FormatString(c => c.Information($"{new string('7', 60)}{123456789}"), 64));
        Assert.Equal(0, after.FormatCount);
    }

    [Fact]
    public void Format_ExactlyAtMaximum_IsNotTruncated()
    {
        string text = new('a', 62);

        Assert.Equal(text + "\u00e9", FormatString(c => c.Information($"{text}{'\u00e9'}"), 64));
        Assert.Equal(new string('a', 61) + Ellipsis, FormatString(c => c.Information($"{text}{'\u00e9'}{'b'}"), 64));
    }

    [Fact]
    public void Format_ValueLargerThanSpaceLeft_IsFormattedThenTruncated()
    {
        Assert.Equal("start " + new string('h', 55) + Ellipsis, FormatString(c => c.Information($"start {new Huge(100_000)} end"), 64));
        Assert.Equal("start " + Ellipsis, FormatString(c => c.Information($"start {new Huge(2_000_000)} end"), 64));
    }

    [Fact]
    public void TruncatedMessage_IsAlwaysValidUtf8()
    {
        var text = Gen.OneOf(
            Gen.Char.Array[0, 200].Select(chars => new string(chars)),
            Gen.Int[0, 0x10FFFF].Where(Rune.IsValid).Array[0, 120].Select(scalars => string.Concat(Array.ConvertAll(scalars, s => new Rune(s).ToString()))));

        Gen.Select(text, text, Gen.Int[64, 300]).Sample((first, second, limit) =>
        {
            byte[] expected = Encoding.UTF8.GetBytes(first + "|" + second);
            byte[] actual = Format(c => c.Information($"{first}|{second.AsSpan()}"), limit);

            Assert.True(Utf8.IsValid(actual));
            Assert.True(actual.Length <= limit);
            if (expected.Length <= limit)
            {
                Assert.Equal(expected, actual);
                return;
            }

            Assert.True(actual.AsSpan().EndsWith("\u2026"u8));
            ReadOnlySpan<byte> kept = actual.AsSpan(0, actual.Length - 3);
            Assert.True(expected.AsSpan().StartsWith(kept));
            Assert.True(kept.Length > limit - 3 - 4, "At most one code point more than needed is cut.");
        });
    }

    [Fact]
    public void ArbitraryBytes_AlwaysGiveValidUtf8()
    {
        Gen.Select(Gen.Byte.Array[0, 300], Gen.Int[64, 300]).Sample((bytes, limit) =>
        {
            byte[] actual = Format(c => c.Warning($"{bytes.AsSpan()}"), limit);

            Assert.True(Utf8.IsValid(actual));
            Assert.True(actual.Length <= limit);
        });
    }

    private static byte[] Format(Action<LogCategory> log, int maxMessageBytes = 4096)
    {
        RecordingSink sink = new();
        using Logger logger = new(new LoggerOptions { Sinks = [sink], MaxMessageBytes = maxMessageBytes });
        log(logger.CreateCategory("Test"));
        return Assert.Single(sink.Entries).MessageBytes;
    }

    private static string FormatString(Action<LogCategory> log, int maxMessageBytes = 4096) =>
        Encoding.UTF8.GetString(Format(log, maxMessageBytes));

    private readonly struct SpanOnly(double value) : ISpanFormattable
    {
        public string ToString(string? format, IFormatProvider? formatProvider) => throw new InvalidOperationException("Should use TryFormat.");

        public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider) =>
            value.TryFormat(destination, out charsWritten, format, provider);
    }

    private sealed class FormattableOnly(double value) : IFormattable
    {
        public string ToString(string? format, IFormatProvider? formatProvider) => value.ToString(format, formatProvider);
    }

    private sealed class PlainObject
    {
        public override string ToString() => "plain";
    }

    /// <summary>Writes invalid UTF-8 (a lone 0xC0) followed by "ok".</summary>
    private readonly struct BrokenUtf8 : IUtf8SpanFormattable
    {
        public bool TryFormat(Span<byte> utf8Destination, out int bytesWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
        {
            bytesWritten = 0;
            if (utf8Destination.Length < 3)
            {
                return false;
            }

            utf8Destination[0] = 0xC0;
            utf8Destination[1] = (byte)'o';
            utf8Destination[2] = (byte)'k';
            bytesWritten = 3;
            return true;
        }
    }

    /// <summary>Formats as <paramref name="size"/> letters <c>h</c>: more than any message holds.</summary>
    private readonly struct Huge(int size) : IUtf8SpanFormattable
    {
        public bool TryFormat(Span<byte> utf8Destination, out int bytesWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
        {
            bytesWritten = 0;
            if (utf8Destination.Length < size)
            {
                return false;
            }

            utf8Destination[..size].Fill((byte)'h');
            bytesWritten = size;
            return true;
        }
    }
}
