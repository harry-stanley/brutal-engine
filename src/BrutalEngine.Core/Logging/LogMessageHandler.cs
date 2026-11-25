using System;
using System.Buffers;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Unicode;

namespace BrutalEngine.Core.Logging;

/// <summary>
/// Interpolated string handler behind the logging methods of <see cref="LogCategory"/>. It checks the category's
/// level first, and only if enabled formats the message straight to UTF-8.
/// </summary>
/// <typeparam name="TLevel">Marker for the level of the logging method, or <see cref="DynamicLevel"/>.</typeparam>
/// <remarks>
/// <para>
/// Created by the compiler for each logging call; there's no need to use it directly. When the level is disabled,
/// no part of the message is formatted and nothing is rented. When enabled, the message is formatted into a
/// buffer of <see cref="LoggerOptions.MaxMessageBytes"/> bytes rented from <see cref="ArrayPool{T}.Shared"/>,
/// and the buffer is returned once the entry has been dispatched.
/// </para>
/// <para>
/// Formatting uses the invariant culture. The output is always valid UTF-8: invalid UTF-16 or UTF-8 input is
/// replaced with U+FFFD. A message longer than the maximum is cut on a code-point boundary and ends with
/// <c>…</c> (U+2026); later parts aren't formatted.
/// </para>
/// <para>
/// No allocation for <see cref="IUtf8SpanFormattable"/> values (all numeric types, <see cref="char"/>,
/// <see cref="Guid"/>, dates and times, and custom value types that implement it), <see cref="bool"/>, strings and
/// spans. Other values, including enums and nullable values such as <c>int?</c>, go through
/// <see cref="AppendFormatted(object?, string?)"/>, which boxes them. So do a struct that implements only
/// <see cref="ISpanFormattable"/>, and a value of a generic type parameter that isn't constrained to
/// <see cref="IUtf8SpanFormattable"/>, even when the type argument is <see cref="int"/>: the compiler picks the
/// overload from the declared type. To keep such values allocation-free, implement <see cref="IUtf8SpanFormattable"/>
/// on the struct, and constrain the generic parameter (<c>where T : IUtf8SpanFormattable</c>). If formatting a value
/// throws, the exception reaches the caller, nothing is logged, and the rented buffer is left to the garbage
/// collector.
/// </para>
/// <para>
/// Confined to the calling thread, like any <see langword="ref"/> struct. <b>Don't copy a handler:</b> each copy
/// would return the same pooled buffer when completed, which corrupts the pool. The compiler never copies one.
/// </para>
/// </remarks>
[InterpolatedStringHandler]
public ref struct LogMessageHandler<TLevel>
    where TLevel : struct, ILogLevel
{
    private const int CharScratchLength = 256;
    private const int MaxOverflowScratchBytes = 1 << 20;

    private readonly LogCategoryState? _category;
    private readonly LogLevel _level;
    private readonly int _limit;
    private byte[]? _buffer;
    private int _length;
    private bool _truncated;

    /// <summary>Starts a message at the level of <typeparamref name="TLevel"/>. Called by the compiler.</summary>
    /// <param name="literalLength">Total length of the literal parts (unused).</param>
    /// <param name="formattedCount">Number of interpolation holes (unused).</param>
    /// <param name="category">The category the message is logged through.</param>
    /// <param name="isEnabled">Set to whether the level is enabled; if not, the compiler skips every part.</param>
    public LogMessageHandler(int literalLength, int formattedCount, LogCategory category, out bool isEnabled)
        : this(literalLength, formattedCount, category, TLevel.Level, out isEnabled)
    {
    }

    /// <summary>Starts a message at an explicit level. Called by the compiler for <see cref="LogCategory.Log"/>.</summary>
    /// <param name="literalLength">Total length of the literal parts (unused).</param>
    /// <param name="formattedCount">Number of interpolation holes (unused).</param>
    /// <param name="category">The category the message is logged through.</param>
    /// <param name="level">The level of the message.</param>
    /// <param name="isEnabled">Set to whether the level is enabled; if not, the compiler skips every part.</param>
    public LogMessageHandler(int literalLength, int formattedCount, LogCategory category, LogLevel level, out bool isEnabled)
    {
        // The lengths are part of the handler pattern but unused: the buffer size is fixed by MaxMessageBytes.
        _ = literalLength;
        _ = formattedCount;
        _level = level;
        LogCategoryState? state = category.State;
        isEnabled = state is not null && state.IsEnabled(level);
        if (isEnabled)
        {
            _category = state;
            _limit = state!.Owner.MaxMessageBytes;
            _buffer = ArrayPool<byte>.Shared.Rent(_limit);
        }
    }

    private static ReadOnlySpan<byte> Ellipsis => "\u2026"u8;

    /// <summary>Appends a literal part of the message.</summary>
    /// <param name="value">The literal.</param>
    public void AppendLiteral(string value) => AppendChars(value);

    /// <summary>Appends a string. <see langword="null"/> appends nothing.</summary>
    /// <param name="value">The string.</param>
    public void AppendFormatted(string? value) => AppendChars(value);

    /// <summary>Appends UTF-16 characters.</summary>
    /// <param name="value">The characters.</param>
    public void AppendFormatted(ReadOnlySpan<char> value) => AppendChars(value);

    /// <summary>Appends UTF-8 text. Invalid sequences are replaced with U+FFFD.</summary>
    /// <param name="utf8">The UTF-8 bytes.</param>
    public void AppendFormatted(ReadOnlySpan<byte> utf8)
    {
        if (_buffer is not null && !_truncated)
        {
            AppendUtf8(utf8);
        }
    }

    /// <summary>Appends <c>True</c> or <c>False</c>, as <see cref="bool.ToString()"/> does.</summary>
    /// <param name="value">The value.</param>
    public void AppendFormatted(bool value) => AppendFormatted(value ? "True"u8 : "False"u8);

    /// <summary>Appends a value that formats itself to UTF-8, with the invariant culture.</summary>
    /// <typeparam name="T">The value's type: any <see cref="IUtf8SpanFormattable"/>, including nullable references.</typeparam>
    /// <param name="value">The value. <see langword="null"/> appends nothing.</param>
    /// <remarks>
    /// Never boxes a value type; see <see cref="AppendFormatted{T}(T, string?)"/>, including which values miss this
    /// overload.
    /// </remarks>
    public void AppendFormatted<T>(T value)
        where T : IUtf8SpanFormattable? => AppendFormatted(value, null);

    /// <summary>Appends a value that formats itself to UTF-8, with a format string and the invariant culture.</summary>
    /// <typeparam name="T">
    /// The value's type: any <see cref="IUtf8SpanFormattable"/>, such as the numeric types, <see cref="char"/>,
    /// <see cref="Guid"/>, the date and time types, or the engine's own maths types. Nullable references are
    /// accepted.
    /// </typeparam>
    /// <param name="value">The value. <see langword="null"/> appends nothing.</param>
    /// <param name="format">Format string, e.g. <c>F2</c> or <c>X8</c>, or <see langword="null"/>.</param>
    /// <remarks>
    /// <para>
    /// Formats straight into the buffer through a constrained call, so a value type is never boxed: no
    /// allocation, whatever the JIT optimises (unoptimised and coverage-instrumented code included).
    /// </para>
    /// <para>
    /// The compiler picks this overload from the value's declared type. A value of a generic type parameter that
    /// isn't constrained to <see cref="IUtf8SpanFormattable"/> misses it, whatever the type argument, and so does a
    /// struct that implements only <see cref="ISpanFormattable"/>: both take the allocating
    /// <see cref="AppendFormatted(object?, string?)"/>. Constrain your generic parameter
    /// (<c>where T : IUtf8SpanFormattable</c>) or implement <see cref="IUtf8SpanFormattable"/> to stay here.
    /// </para>
    /// </remarks>
    public void AppendFormatted<T>(T value, string? format)
        where T : IUtf8SpanFormattable?
    {
        // The IsValueType test keeps the null check (a box) out of value-type instantiations in unoptimised code.
        if (_buffer is null || _truncated || (!typeof(T).IsValueType && value is null))
        {
            return;
        }

        Span<byte> destination = _buffer.AsSpan(_length, _limit - _length);
        // Not null: a null reference returned above, and a value type can't be null.
        if (value!.TryFormat(destination, out int written, format, CultureInfo.InvariantCulture))
        {
            CommitFormatted(written);
        }
        else
        {
            AppendOverflowing(value, format);
        }
    }

    /// <summary>Appends any other value, formatted with the invariant culture where it supports one.</summary>
    /// <param name="value">The value. <see langword="null"/> appends nothing.</param>
    /// <remarks><b>Allocates</b>; see <see cref="AppendFormatted(object?, string?)"/>.</remarks>
    public void AppendFormatted(object? value) => AppendFormatted(value, null);

    /// <summary>Appends any other value with a format string, formatted with the invariant culture where it supports one.</summary>
    /// <param name="value">The value. <see langword="null"/> appends nothing.</param>
    /// <param name="format">Format string, passed on if the value is formattable, or <see langword="null"/>.</param>
    /// <remarks>
    /// <para>
    /// The fallback for values whose declared type isn't <see cref="IUtf8SpanFormattable"/>. It tries
    /// <see cref="IUtf8SpanFormattable"/> on the boxed value, then <see cref="ISpanFormattable"/>, then
    /// <see cref="IFormattable.ToString(string?, IFormatProvider?)"/>, then <see cref="object.ToString"/>.
    /// </para>
    /// <para>
    /// <b>Allocates:</b> value types are boxed to get here, and <c>ToString</c> allocates a string. That includes
    /// enums, nullable values such as <c>int?</c> (24 bytes per value), structs that implement only
    /// <see cref="ISpanFormattable"/>, and values of a generic type parameter that isn't constrained to
    /// <see cref="IUtf8SpanFormattable"/>, even when the type argument is a number. To avoid it, implement
    /// <see cref="IUtf8SpanFormattable"/>, or constrain the generic parameter (<c>where T : IUtf8SpanFormattable</c>),
    /// so <see cref="AppendFormatted{T}(T, string?)"/> is chosen.
    /// </para>
    /// </remarks>
    public void AppendFormatted(object? value, string? format)
    {
        if (_buffer is null || _truncated || value is null)
        {
            return;
        }

        if (value is IUtf8SpanFormattable utf8Formattable)
        {
            AppendFormatted(utf8Formattable, format);
            return;
        }

        if (value is ISpanFormattable spanFormattable)
        {
            Span<char> chars = stackalloc char[CharScratchLength];
            if (spanFormattable.TryFormat(chars, out int charsWritten, format, CultureInfo.InvariantCulture))
            {
                AppendChars(chars[..charsWritten]);
                return;
            }
        }

        AppendChars(value is IFormattable formattable
            ? formattable.ToString(format, CultureInfo.InvariantCulture)
            : value.ToString());
    }

    /// <summary>
    /// Dispatches the finished message, then returns the buffer. Safe to call more than once on the same handler,
    /// but not on copies of it (see the type's remarks). The logging call passes its own category and level: a
    /// handler built by hand for another one is misuse.
    /// </summary>
    internal void Complete(LogCategoryState? category, LogLevel level, string callerFile, int callerLine)
    {
        byte[]? buffer = _buffer;
        if (buffer is null)
        {
            return;
        }

        _buffer = null;
        try
        {
            if (!ReferenceEquals(category, _category) || level != _level)
            {
                throw new ArgumentException(
                    "The message handler was created for another category or level than the logging call. Pass an interpolated string and let the compiler create the handler.");
            }

            _category!.Owner.Dispatch(_category, _level, buffer.AsSpan(0, _length), callerFile, callerLine);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static bool IsContinuationByte(byte value) => (value & 0xC0) == 0x80;

    private void AppendChars(scoped ReadOnlySpan<char> chars)
    {
        if (_buffer is null || _truncated)
        {
            return;
        }

        // Replaces lone surrogates with U+FFFD, and stops before a code point that doesn't fit.
        OperationStatus status = Utf8.FromUtf16(chars, _buffer.AsSpan(_length, _limit - _length), out _, out int written);
        _length += written;
        if (status == OperationStatus.DestinationTooSmall)
        {
            Truncate();
        }
    }

    private void AppendUtf8(scoped ReadOnlySpan<byte> utf8)
    {
        if (Utf8.IsValid(utf8))
        {
            AppendValidUtf8(utf8);
            return;
        }

        Span<byte> encoded = stackalloc byte[4];
        while (!utf8.IsEmpty && !_truncated)
        {
            if (Rune.DecodeFromUtf8(utf8, out Rune rune, out int consumed) != OperationStatus.Done)
            {
                rune = Rune.ReplacementChar;
            }

            AppendValidUtf8(encoded[..rune.EncodeToUtf8(encoded)]);
            utf8 = utf8[consumed..];
        }
    }

    private void AppendValidUtf8(scoped ReadOnlySpan<byte> utf8)
    {
        Span<byte> destination = _buffer.AsSpan(_length, _limit - _length);
        if (utf8.Length <= destination.Length)
        {
            utf8.CopyTo(destination);
            _length += utf8.Length;
            return;
        }

        // Fill the buffer, possibly ending inside a code point: Truncate cuts back to a code-point boundary below
        // the ellipsis anyway, which always removes this tail.
        utf8[..destination.Length].CopyTo(destination);
        _length += destination.Length;
        Truncate();
    }

    // A value formatted in place by an IUtf8SpanFormattable. Custom formatters can write invalid UTF-8: re-append
    // their output through the replacing path (rare, so the copy is fine).
    private void CommitFormatted(int written)
    {
        Span<byte> formatted = _buffer.AsSpan(_length, written);
        if (Utf8.IsValid(formatted))
        {
            _length += written;
            return;
        }

        byte[] copy = ArrayPool<byte>.Shared.Rent(written);
        formatted.CopyTo(copy);
        AppendUtf8(copy.AsSpan(0, written));
        ArrayPool<byte>.Shared.Return(copy);
    }

    // The value didn't fit the space left: format it into a larger temporary buffer, then append what fits.
    // Callers have already returned on null.
    private void AppendOverflowing<TFormattable>(TFormattable formattable, string? format)
        where TFormattable : IUtf8SpanFormattable?
    {
        for (int size = Math.Max(_limit, CharScratchLength) * 2; size <= MaxOverflowScratchBytes; size *= 2)
        {
            byte[] scratch = ArrayPool<byte>.Shared.Rent(size);
            try
            {
                if (formattable!.TryFormat(scratch, out int written, format, CultureInfo.InvariantCulture))
                {
                    AppendUtf8(scratch.AsSpan(0, written));
                    return;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(scratch);
            }
        }

        // Larger than any message could be: it would be cut anyway.
        Truncate();
    }

    // Ends the message with the ellipsis, within the limit and on a code-point boundary. Later appends are ignored.
    private void Truncate()
    {
        _truncated = true;
        byte[] buffer = _buffer!;
        int cut = Math.Min(_length, _limit - Ellipsis.Length);

        // buffer[cut] is the first byte left out (when cut < _length): back up until it starts a code point.
        while (cut > 0 && cut < _length && IsContinuationByte(buffer[cut]))
        {
            cut--;
        }

        Ellipsis.CopyTo(buffer.AsSpan(cut));
        _length = cut + Ellipsis.Length;
    }
}
