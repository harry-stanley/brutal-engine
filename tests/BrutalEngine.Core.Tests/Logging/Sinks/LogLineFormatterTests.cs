using System;
using System.Text;
using System.Text.Unicode;
using BrutalEngine.Core.Logging;
using BrutalEngine.Core.Logging.Sinks;
using CsCheck;
using Xunit;

namespace BrutalEngine.Core.Tests.Logging.Sinks;

public sealed class LogLineFormatterTests
{
    // 2026-11-25 16:04:05.6789999 at UTC+13:00, which is 03:04:05.6789999 UTC.
    private static readonly DateTimeOffset _localTime = new(new DateTime(2026, 11, 25, 16, 4, 5).AddTicks(6_789_999), TimeSpan.FromHours(13));

    [Fact]
    public void Format_AllFields_MatchesSpec()
    {
        LogEntry entry = new()
        {
            Level = LogLevel.Information,
            Category = "Render.Vulkan",
            Timestamp = _localTime,
            ThreadId = 7,
            Message = "w\u00f6rld\u2026 \U0001F600\nsecond"u8,
            CallerFilePath = "C:/src/Renderer.cs",
            CallerLineNumber = 42,
        };

        byte[] line = FormatToArray(entry, includeCallerInfo: false);

        // UTC, milliseconds truncated (not rounded), no caller suffix, the message as is (newline included).
        Assert.Equal("2026-11-25T03:04:05.678Z INFO [Render.Vulkan] w\u00f6rld\u2026 \U0001F600\nsecond\n"u8.ToArray(), line);
    }

    [Theory]
    [InlineData(LogLevel.Trace, "TRCE")]
    [InlineData(LogLevel.Debug, "DBUG")]
    [InlineData(LogLevel.Information, "INFO")]
    [InlineData(LogLevel.Warning, "WARN")]
    [InlineData(LogLevel.Error, "EROR")]
    [InlineData(LogLevel.Critical, "CRIT")]
    public void Format_EachLevel_WritesFixedWidthToken(LogLevel level, string token)
    {
        LogEntry entry = new() { Level = level, Category = "Core", Timestamp = DateTimeOffset.UnixEpoch, Message = "m"u8 };

        Assert.Equal(Encoding.UTF8.GetBytes($"1970-01-01T00:00:00.000Z {token} [Core] m\n"), FormatToArray(entry, includeCallerInfo: false));
    }

    [Fact]
    public void Format_WithCallerInfo_AppendsFileAndLine()
    {
        LogEntry entry = new()
        {
            Level = LogLevel.Error,
            Category = "Audio",
            Timestamp = new DateTimeOffset(9999, 12, 31, 23, 59, 59, 999, TimeSpan.Zero),
            Message = "device lost"u8,
            CallerFilePath = "/src/\u00c4udio/Mixer.cs",
            CallerLineNumber = 1234,
        };

        Assert.Equal(
            "9999-12-31T23:59:59.999Z EROR [Audio] device lost (/src/\u00c4udio/Mixer.cs:1234)\n"u8.ToArray(),
            FormatToArray(entry, includeCallerInfo: true));
    }

    [Fact]
    public void Format_WithCallerInfoButNoFile_OmitsSuffix()
    {
        LogEntry entry = new() { Level = LogLevel.Warning, Category = "Core", Timestamp = DateTimeOffset.UnixEpoch, CallerLineNumber = 5 };

        Assert.Equal("1970-01-01T00:00:00.000Z WARN [Core] \n"u8.ToArray(), FormatToArray(entry, includeCallerInfo: true));
    }

    [Fact]
    public void Format_LoneSurrogatesInStrings_WritesReplacementCharacter()
    {
        LogEntry entry = new()
        {
            Level = LogLevel.Debug,
            Category = "A\uD800",
            Timestamp = DateTimeOffset.UnixEpoch,
            CallerFilePath = "\uDC00.cs",
            CallerLineNumber = -1,
        };

        Assert.Equal(
            "1970-01-01T00:00:00.000Z DBUG [A\uFFFD]  (\uFFFD.cs:-1)\n"u8.ToArray(),
            FormatToArray(entry, includeCallerInfo: true));
    }

    [Theory]
    [InlineData(LogLevel.None)]
    [InlineData((LogLevel)42)]
    public void Format_LevelOutOfRange_ThrowsArgumentOutOfRange(LogLevel level)
    {
        byte[] buffer = new byte[256];

        _ = Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            LogEntry entry = new() { Level = level, Category = "Core" };
            return LogLineFormatter.Format(entry, includeCallerInfo: false, buffer);
        });
    }

    [Fact]
    public void Format_DestinationShorterThanMaxLength_ThrowsArgument()
    {
        _ = Assert.Throws<ArgumentException>(() =>
        {
            LogEntry entry = new() { Level = LogLevel.Information, Category = "Core", Message = "message"u8 };
            byte[] buffer = new byte[LogLineFormatter.GetMaxLength(entry, includeCallerInfo: false) - 1];
            return LogLineFormatter.Format(entry, includeCallerInfo: false, buffer);
        });
    }

    [Fact]
    public void Format_RandomEntries_FitMaxLengthAndAreValidUtf8()
    {
        Gen<string> text = Gen.Char.Array[0, 80].Select(chars => new string(chars));
        Gen.Select(text, text, Gen.Int, Gen.Bool).Sample((category, file, line, includeCallerInfo) =>
        {
            LogEntry entry = new()
            {
                Level = LogLevel.Critical,
                Category = category,
                Timestamp = DateTimeOffset.UnixEpoch,
                Message = "message"u8,
                CallerFilePath = file,
                CallerLineNumber = line,
            };

            byte[] buffer = new byte[LogLineFormatter.GetMaxLength(entry, includeCallerInfo)];
            int written = LogLineFormatter.Format(entry, includeCallerInfo, buffer);

            Assert.True(Utf8.IsValid(buffer.AsSpan(0, written)));
            Assert.Equal((byte)'\n', buffer[written - 1]);
            string expectedSuffix = includeCallerInfo && file.Length > 0 ? $" ({file}:{line})" : string.Empty;
            Assert.Equal(
                Encoding.UTF8.GetBytes($"1970-01-01T00:00:00.000Z CRIT [{category}] message{expectedSuffix}\n"),
                buffer.AsSpan(0, written).ToArray());
        });
    }

    private static byte[] FormatToArray(scoped in LogEntry entry, bool includeCallerInfo)
    {
        byte[] buffer = new byte[LogLineFormatter.GetMaxLength(entry, includeCallerInfo)];
        int written = LogLineFormatter.Format(entry, includeCallerInfo, buffer);
        return buffer.AsSpan(0, written).ToArray();
    }
}
