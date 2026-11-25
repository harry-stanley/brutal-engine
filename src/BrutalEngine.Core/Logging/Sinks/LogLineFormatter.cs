using System;
using System.Buffers;
using System.Globalization;
using System.Text.Unicode;

namespace BrutalEngine.Core.Logging.Sinks;

/// <summary>
/// Formats a <see cref="LogEntry"/> as one UTF-8 text line, for the console and file sinks:
/// <c>yyyy-MM-ddTHH:mm:ss.fffZ LEVEL [Category] message\n</c>, plus <c> (file:line)</c> before the newline when
/// caller info is requested and the entry has a caller file.
/// </summary>
/// <remarks>
/// <para>
/// The timestamp is converted to UTC and its fraction truncated to milliseconds, as the <c>fff</c> format does.
/// LEVEL is a fixed-width token: <c>TRCE DBUG INFO WARN EROR CRIT</c>. The message is copied as received
/// (<see cref="LogEntry.Message"/> is valid UTF-8 by contract), newlines included. The category and the caller
/// file are encoded to UTF-8, with lone surrogates replaced by U+FFFD.
/// </para>
/// <para>Stateless and thread-safe. Nothing allocates.</para>
/// </remarks>
internal static class LogLineFormatter
{
    // "yyyy-MM-ddTHH:mm:ss.fffZ" (24), " LEVEL" (5), " [" and "] " (4), "\n" (1).
    private const int FixedLength = 34;

    // " (", ":", the line number (at most 11 characters, for int.MinValue) and ")".
    private const int CallerSuffixFixedLength = 15;

    /// <summary>The largest number of bytes <see cref="Format"/> can write for this entry.</summary>
    /// <param name="entry">The entry.</param>
    /// <param name="includeCallerInfo">Whether the caller suffix is wanted.</param>
    /// <returns>An upper bound on the line length in bytes.</returns>
    /// <exception cref="OverflowException">The entry's strings are too long for any line buffer.</exception>
    public static int GetMaxLength(scoped in LogEntry entry, bool includeCallerInfo)
    {
        checked
        {
            // A UTF-16 code unit never needs more than 3 UTF-8 bytes (a surrogate pair needs 4 for 2 units).
            int length = FixedLength + (entry.Category.Length * 3) + entry.Message.Length;
            if (HasCallerSuffix(entry, includeCallerInfo))
            {
                length += CallerSuffixFixedLength + (entry.CallerFilePath.Length * 3);
            }

            return length;
        }
    }

    /// <summary>Writes the entry's line into <paramref name="destination"/>.</summary>
    /// <param name="entry">The entry.</param>
    /// <param name="includeCallerInfo">Whether to append <c> (file:line)</c>. Left out when the caller file is empty.</param>
    /// <param name="destination">Where to write. At least <see cref="GetMaxLength"/> bytes long.</param>
    /// <returns>The number of bytes written, newline included.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The entry's level is outside Trace to Critical.</exception>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is shorter than <see cref="GetMaxLength"/>.</exception>
    public static int Format(scoped in LogEntry entry, bool includeCallerInfo, Span<byte> destination)
    {
        ThrowIfInvalidLevel(entry.Level, nameof(entry));
        if (destination.Length < GetMaxLength(entry, includeCallerInfo))
        {
            throw new ArgumentException("The destination is shorter than GetMaxLength for this entry.", nameof(destination));
        }

        int position = WriteTimestamp(entry.Timestamp.UtcDateTime, destination);
        position += Copy(" "u8, destination[position..]);
        position += Copy(LevelToken(entry.Level), destination[position..]);
        position += Copy(" ["u8, destination[position..]);
        position += Encode(entry.Category, destination[position..]);
        position += Copy("] "u8, destination[position..]);
        position += Copy(entry.Message, destination[position..]);

        if (HasCallerSuffix(entry, includeCallerInfo))
        {
            position += Copy(" ("u8, destination[position..]);
            position += Encode(entry.CallerFilePath, destination[position..]);
            position += Copy(":"u8, destination[position..]);

            // Can't fail: GetMaxLength reserved 11 bytes, enough for any int.
            _ = entry.CallerLineNumber.TryFormat(destination[position..], out int written, default, CultureInfo.InvariantCulture);
            position += written;
            position += Copy(")"u8, destination[position..]);
        }

        position += Copy("\n"u8, destination[position..]);
        return position;
    }

    /// <summary>Throws if <paramref name="level"/> can't be written: anything outside Trace to Critical.</summary>
    /// <param name="level">The entry's level.</param>
    /// <param name="paramName">The name of the entry parameter, for the exception.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="level"/> is outside Trace to Critical.</exception>
    public static void ThrowIfInvalidLevel(LogLevel level, string paramName)
    {
        // LogLevel is a byte, so Trace (0) is also the lowest possible value.
        if (level > LogLevel.Critical)
        {
            throw new ArgumentOutOfRangeException(paramName, level, "The entry's level must be from Trace to Critical.");
        }
    }

    private static ReadOnlySpan<byte> LevelToken(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRCE"u8,
        LogLevel.Debug => "DBUG"u8,
        LogLevel.Information => "INFO"u8,
        LogLevel.Warning => "WARN"u8,
        LogLevel.Error => "EROR"u8,
        _ => "CRIT"u8,
    };

    private static bool HasCallerSuffix(scoped in LogEntry entry, bool includeCallerInfo) =>
        includeCallerInfo && entry.CallerFilePath.Length > 0;

    private static int Copy(scoped ReadOnlySpan<byte> source, Span<byte> destination)
    {
        source.CopyTo(destination);
        return source.Length;
    }

    // Replaces lone surrogates with U+FFFD. GetMaxLength allows 3 bytes per code unit, so it always fits.
    private static int Encode(string text, Span<byte> destination)
    {
        OperationStatus status = Utf8.FromUtf16(text, destination, out _, out int written);
        return status == OperationStatus.Done
            ? written
            : throw new InvalidOperationException($"UTF-8 encoding stopped early ({status}); GetMaxLength is wrong.");
    }

    // yyyy-MM-ddTHH:mm:ss.fffZ: always 24 bytes, since DateTime years are 1 to 9999.
    private static int WriteTimestamp(DateTime utc, Span<byte> destination)
    {
        WriteDigits(destination, utc.Year, 4);
        destination[4] = (byte)'-';
        WriteDigits(destination[5..], utc.Month, 2);
        destination[7] = (byte)'-';
        WriteDigits(destination[8..], utc.Day, 2);
        destination[10] = (byte)'T';
        WriteDigits(destination[11..], utc.Hour, 2);
        destination[13] = (byte)':';
        WriteDigits(destination[14..], utc.Minute, 2);
        destination[16] = (byte)':';
        WriteDigits(destination[17..], utc.Second, 2);
        destination[19] = (byte)'.';
        WriteDigits(destination[20..], utc.Millisecond, 3);
        destination[23] = (byte)'Z';
        return 24;
    }

    private static void WriteDigits(Span<byte> destination, int value, int count)
    {
        for (int i = count - 1; i >= 0; i--)
        {
            destination[i] = (byte)('0' + (value % 10));
            value /= 10;
        }
    }
}
