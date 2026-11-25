using System;
using System.IO;
using System.Threading;

namespace BrutalEngine.Core.Logging.Sinks;

/// <summary>
/// A sink that writes each entry as one UTF-8 text line to standard output or standard error, or to streams
/// the host provides.
/// </summary>
/// <remarks>
/// <para>
/// Line format: <c>yyyy-MM-ddTHH:mm:ss.fffZ LEVEL [Category] message</c> and a <c>\n</c>, with the timestamp in
/// UTC and LEVEL one of <c>TRCE DBUG INFO WARN EROR CRIT</c>. With
/// <see cref="ConsoleLogSinkOptions.IncludeCallerInfo"/>, <c> (file:line)</c> goes before the newline when the
/// entry has a caller file. Messages are written as received, newlines included.
/// </para>
/// <para>
/// Entries at or above <see cref="ErrorThreshold"/> go to the error output, the rest to the output. Each line is
/// written with a single <see cref="Stream.Write(byte[], int, int)"/> call. The stream is flushed after every
/// <see cref="LogLevel.Error"/> or <see cref="LogLevel.Critical"/> entry, and by <see cref="Flush"/>.
/// </para>
/// <para>
/// Thread-safe: every member may be called from any thread, and lines from concurrent writers never interleave.
/// </para>
/// <para>
/// Ownership: the sink owns, and <see cref="Dispose"/> disposes, the standard streams it opened itself. It never
/// disposes streams passed in <see cref="ConsoleLogSinkOptions"/>. Dispose the sink after the loggers that use it.
/// </para>
/// <para>
/// Allocation: the sink keeps a line buffer that grows only when a line is longer than any before it, so
/// steady-state writes allocate no managed memory (as long as the streams' own writes don't).
/// </para>
/// </remarks>
public sealed class ConsoleLogSink : ILogSink, IDisposable
{
    private const int InitialLineBufferBytes = 8192;

    private readonly Lock _lock = new();
    private readonly Stream _output;
    private readonly Stream _errorOutput;
    private readonly bool _ownsOutput;
    private readonly bool _ownsErrorOutput;
    private byte[] _line = new byte[InitialLineBufferBytes];
    private bool _disposed;

    /// <summary>Creates a console sink.</summary>
    /// <param name="options">Settings. Standard streams are opened for the outputs left <see langword="null"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A stream in <paramref name="options"/> isn't writable.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="ConsoleLogSinkOptions.ErrorThreshold"/> isn't a defined <see cref="LogLevel"/>.
    /// </exception>
    public ConsoleLogSink(ConsoleLogSinkOptions options)
        : this(options, Console.OpenStandardOutput, Console.OpenStandardError)
    {
    }

    /// <summary>Creates a sink that opens its default streams with the given functions (tests use this).</summary>
    internal ConsoleLogSink(ConsoleLogSinkOptions options, Func<Stream> openOutput, Func<Stream> openErrorOutput)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!Enum.IsDefined(options.ErrorThreshold))
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.ErrorThreshold, "ConsoleLogSinkOptions.ErrorThreshold is not a defined LogLevel.");
        }

        if (options.Output is { CanWrite: false } || options.ErrorOutput is { CanWrite: false })
        {
            throw new ArgumentException("The streams in ConsoleLogSinkOptions must be writable.", nameof(options));
        }

        ErrorThreshold = options.ErrorThreshold;
        IncludeCallerInfo = options.IncludeCallerInfo;
        _ownsOutput = options.Output is null;
        _ownsErrorOutput = options.ErrorOutput is null;
        _output = options.Output ?? openOutput();
        try
        {
            _errorOutput = options.ErrorOutput ?? openErrorOutput();
        }
        catch
        {
            if (_ownsOutput)
            {
                _output.Dispose();
            }

            throw;
        }
    }

    /// <summary>The lowest level written to the error output.</summary>
    /// <remarks>Fixed at construction. Thread-safe; doesn't allocate.</remarks>
    public LogLevel ErrorThreshold { get; }

    /// <summary>Whether lines end with the caller's <c> (file:line)</c>.</summary>
    /// <remarks>Fixed at construction. Thread-safe; doesn't allocate.</remarks>
    public bool IncludeCallerInfo { get; }

    /// <summary>Writes the entry as one line, and flushes if its level is <see cref="LogLevel.Error"/> or above.</summary>
    /// <param name="entry">The entry. Nothing of it is kept after the call returns.</param>
    /// <remarks>Thread-safe. Allocates only when the line is longer than any written before.</remarks>
    /// <exception cref="ArgumentOutOfRangeException">The entry's level is outside Trace to Critical.</exception>
    /// <exception cref="ObjectDisposedException">The sink is disposed.</exception>
    /// <exception cref="IOException">The stream failed; exceptions from the stream propagate.</exception>
    /// <exception cref="OverflowException">
    /// No line buffer can hold the line: its worst-case length (the message's bytes, 3 bytes per character of the
    /// category and caller file, and the fixed parts) is more than <see cref="int.MaxValue"/>. That takes an
    /// oversized message (close to 2 GB), or a category or caller file hundreds of millions of characters long. A
    /// logger never passes such a message: it caps messages at <see cref="LoggerOptions.MaxMessageBytes"/>.
    /// </exception>
    /// <exception cref="OutOfMemoryException">
    /// Growing the line buffer for an extremely long line failed. Unlike other sink exceptions, a logger doesn't
    /// isolate this one: it reaches the code that logged.
    /// </exception>
    public void Write(scoped in LogEntry entry)
    {
        LogLineFormatter.ThrowIfInvalidLevel(entry.Level, nameof(entry));
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            int maxLength = LogLineFormatter.GetMaxLength(entry, IncludeCallerInfo);
            if (maxLength > _line.Length)
            {
                // Grow geometrically so a run of slightly longer lines doesn't reallocate each time.
                _line = new byte[Math.Max(maxLength, (int)Math.Min((long)_line.Length * 2, Array.MaxLength))];
            }

            int length = LogLineFormatter.Format(entry, IncludeCallerInfo, _line);
            Stream stream = entry.Level >= ErrorThreshold ? _errorOutput : _output;
            stream.Write(_line, 0, length);
            if (entry.Level >= LogLevel.Error)
            {
                stream.Flush();
            }
        }
    }

    /// <summary>Flushes the output, then the error output.</summary>
    /// <remarks>Thread-safe; doesn't allocate.</remarks>
    /// <exception cref="ObjectDisposedException">The sink is disposed.</exception>
    /// <exception cref="IOException">A stream failed; exceptions from the streams propagate.</exception>
    public void Flush()
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            FlushStreams();
        }
    }

    /// <summary>
    /// Flushes both outputs, then disposes the standard streams the sink opened (never the ones it was given).
    /// Later calls do nothing.
    /// </summary>
    /// <remarks>Thread-safe. Every other member throws <see cref="ObjectDisposedException"/> afterwards.</remarks>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                FlushStreams();
            }
            finally
            {
                try
                {
                    if (_ownsOutput)
                    {
                        _output.Dispose();
                    }
                }
                finally
                {
                    if (_ownsErrorOutput)
                    {
                        _errorOutput.Dispose();
                    }
                }
            }
        }
    }

    // Called with _lock held.
    private void FlushStreams()
    {
        _output.Flush();
        if (!ReferenceEquals(_errorOutput, _output))
        {
            _errorOutput.Flush();
        }
    }
}
