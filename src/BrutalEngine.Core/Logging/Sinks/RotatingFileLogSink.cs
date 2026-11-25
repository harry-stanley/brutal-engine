using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Threading;

namespace BrutalEngine.Core.Logging.Sinks;

/// <summary>
/// A sink that writes each entry as one UTF-8 text line to a log file in a host-provided directory, rotating to a
/// new file by size and keeping a bounded number of files.
/// </summary>
/// <remarks>
/// <para>
/// Lines have the same format as <see cref="ConsoleLogSink"/>'s:
/// <c>yyyy-MM-ddTHH:mm:ss.fffZ LEVEL [Category] message</c> and a <c>\n</c>, with <c> (file:line)</c> before the
/// newline when <see cref="RotatingFileLogSinkOptions.IncludeCallerInfo"/> is set and the entry has a caller file.
/// </para>
/// <para>
/// Files: the current file is <see cref="RotatingFileLogSinkOptions.FileName"/> (default <c>engine.log</c>), and
/// archives are <c>engine.1.log</c> (the newest) to <c>engine.&lt;MaxFiles - 1&gt;.log</c>. Construction rotates
/// an existing current file away, so each run starts a fresh one, and deletes archives beyond the limit left by an
/// earlier run. When a line would take a non-empty file past
/// <see cref="RotatingFileLogSinkOptions.MaxFileBytes"/>, the sink closes it, shifts each archive <c>n</c> to
/// <c>n + 1</c>, deletes the one now beyond the limit, renames the current file to <c>engine.1.log</c> (or deletes
/// it when <see cref="RotatingFileLogSinkOptions.MaxFiles"/> is 1) and opens a new one. A line longer than the
/// maximum is written whole, alone in a fresh file.
/// </para>
/// <para>
/// Writing goes through one buffered file stream, opened with <see cref="FileShare.Read"/> so the log can be read
/// (tailed) while it's written. The stream is flushed to the operating system after every entry at or above
/// <see cref="RotatingFileLogSinkOptions.FlushLevel"/>, by <see cref="Flush"/> and by <see cref="Dispose"/>. Flushed
/// lines survive a crash of the process, but not a power loss: the sink never forces them to the disk (fsync),
/// because doing so on every warning costs too much.
/// </para>
/// <para>
/// Failures: only misuse throws (invalid options, an out-of-range level, use after <see cref="Dispose"/>). I/O
/// never does. The first <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/>, while creating
/// the directory, rotating, opening, writing, flushing or closing, puts the sink in a permanent faulted state:
/// <see cref="Fault"/> describes it, the file is closed, and every later entry is dropped and counted in
/// <see cref="DroppedCount"/>, as is the entry being written when the failure happened. A faulted sink never
/// recovers, even if the cause goes away; the next run's sink starts afresh. A logger therefore never sees a file
/// sink fail: check <see cref="Fault"/> to report it.
/// </para>
/// <para>
/// Thread-safe: every member may be called from any thread, takes the sink's lock, and lines from concurrent
/// writers never interleave. The sink owns its file; dispose it after the loggers that use it.
/// </para>
/// <para>
/// Allocation: writes that don't rotate allocate no managed memory, except when a line is longer than any before
/// it (the line buffer grows). Rotating allocates (file names and a new stream); it happens once per
/// <see cref="RotatingFileLogSinkOptions.MaxFileBytes"/> written, not in steady state.
/// </para>
/// </remarks>
public sealed class RotatingFileLogSink : ILogSink, IDisposable
{
    private const int InitialLineBufferBytes = 8192;
    private const int FileBufferBytes = 16 * 1024;

    private readonly Lock _lock = new();
    private readonly string _path;

    // The archive path up to its number ("<directory>/engine."), and what follows the number (".log").
    private readonly string _archivePrefix;
    private readonly string _archiveSuffix;
    private readonly long _maxFileBytes;
    private readonly int _keptArchives;
    private readonly LogLevel _flushLevel;
    private readonly bool _includeCallerInfo;
    private readonly Func<string, Stream> _openFile;
    private byte[] _line = new byte[InitialLineBufferBytes];

    // Null exactly when the sink is faulted or disposed.
    private Stream? _stream;
    private long _fileBytes;
    private EngineError? _fault;
    private long _droppedCount;
    private bool _disposed;

    /// <summary>
    /// Creates the sink: creates the directory if it's missing, rotates away an existing log file (deleting archives
    /// beyond the limit), and opens a new one.
    /// </summary>
    /// <param name="options">Settings. Every value is validated before the sink touches the disk.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="options"/>, or its <see cref="RotatingFileLogSinkOptions.Directory"/> or
    /// <see cref="RotatingFileLogSinkOptions.FileName"/>, is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <see cref="RotatingFileLogSinkOptions.Directory"/> isn't a fully qualified path or contains a NUL character,
    /// or <see cref="RotatingFileLogSinkOptions.FileName"/> isn't a bare file name (empty, <c>.</c> or <c>..</c>,
    /// or containing a directory separator or a character the platform doesn't allow in file names).
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="RotatingFileLogSinkOptions.MaxFileBytes"/> is less than 64 KiB,
    /// <see cref="RotatingFileLogSinkOptions.MaxFiles"/> is outside 1 to 100, or
    /// <see cref="RotatingFileLogSinkOptions.FlushLevel"/> isn't a defined <see cref="LogLevel"/>.
    /// </exception>
    /// <remarks>
    /// Never throws for I/O: if creating the directory, rotating or opening the file fails, the sink starts
    /// faulted (<see cref="Fault"/> is set) and drops every entry.
    /// </remarks>
    public RotatingFileLogSink(RotatingFileLogSinkOptions options)
        : this(options, OpenFile)
    {
    }

    /// <summary>Creates a sink that opens each new log file with the given function (tests use this).</summary>
    internal RotatingFileLogSink(RotatingFileLogSinkOptions options, Func<string, Stream> openFile)
    {
        ArgumentNullException.ThrowIfNull(options);
        string directory = options.Directory ?? throw new ArgumentNullException(nameof(options), "RotatingFileLogSinkOptions.Directory is null.");
        string fileName = options.FileName ?? throw new ArgumentNullException(nameof(options), "RotatingFileLogSinkOptions.FileName is null.");
        if (!Path.IsPathFullyQualified(directory) || directory.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("RotatingFileLogSinkOptions.Directory must be a fully qualified path.", nameof(options));
        }

        if (fileName.Length == 0 || fileName is "." or ".." || fileName.Contains('/', StringComparison.Ordinal)
            || fileName.Contains('\\', StringComparison.Ordinal) || fileName.AsSpan().IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException("RotatingFileLogSinkOptions.FileName must be a bare file name, with no directory part.", nameof(options));
        }

        if (options.MaxFileBytes < RotatingFileLogSinkOptions.SmallestMaxFileBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.MaxFileBytes, "RotatingFileLogSinkOptions.MaxFileBytes must be at least 64 KiB.");
        }

        if (options.MaxFiles is < 1 or > RotatingFileLogSinkOptions.LargestMaxFiles)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.MaxFiles, "RotatingFileLogSinkOptions.MaxFiles must be from 1 to 100.");
        }

        if (!Enum.IsDefined(options.FlushLevel))
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.FlushLevel, "RotatingFileLogSinkOptions.FlushLevel is not a defined LogLevel.");
        }

        _path = Path.Combine(directory, fileName);
        _archivePrefix = Path.Combine(directory, Path.GetFileNameWithoutExtension(fileName) + ".");
        _archiveSuffix = Path.GetExtension(fileName);
        _maxFileBytes = options.MaxFileBytes;
        _keptArchives = options.MaxFiles - 1;
        _flushLevel = options.FlushLevel;
        _includeCallerInfo = options.IncludeCallerInfo;
        _openFile = openFile;

        try
        {
            _ = Directory.CreateDirectory(directory);
            Rotate(startup: true);
        }
        catch (Exception exception) when (IsIoFailure(exception))
        {
            EnterFault("opening", exception);
        }
    }

    /// <summary>
    /// The I/O failure that faulted the sink, with kind <see cref="ErrorKind.Io"/>, or <see langword="null"/> while
    /// it's healthy. Once set it never changes: the first failure is kept.
    /// </summary>
    /// <remarks>Thread-safe; doesn't allocate.</remarks>
    public EngineError? Fault
    {
        get
        {
            lock (_lock)
            {
                return _fault;
            }
        }
    }

    /// <summary>
    /// How many entries weren't written because the sink is faulted, including the one being written when it
    /// faulted. Zero while it's healthy.
    /// </summary>
    /// <remarks>Thread-safe; doesn't allocate.</remarks>
    public long DroppedCount
    {
        get
        {
            lock (_lock)
            {
                return _droppedCount;
            }
        }
    }

    /// <summary>
    /// Writes the entry as one line, rotating first if the line would take the current file past the maximum size,
    /// and flushes if the entry's level is at or above the flush level.
    /// </summary>
    /// <param name="entry">The entry. Nothing of it is kept after the call returns.</param>
    /// <remarks>
    /// <para>
    /// Never throws for I/O. When the sink is faulted, the entry is dropped and counted in
    /// <see cref="DroppedCount"/>; when writing, flushing or rotating fails, the sink faults and drops this entry.
    /// </para>
    /// <para>Thread-safe. Allocates only when it rotates or faults, or when the line is longer than any before.</para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The entry's level is outside Trace to Critical.</exception>
    /// <exception cref="ObjectDisposedException">The sink is disposed.</exception>
    /// <exception cref="OverflowException">
    /// The entry's category or caller file is so long (hundreds of millions of characters) that no line buffer can
    /// hold the line.
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
            if (_stream is null)
            {
                // Faulted: the sink never writes again.
                _droppedCount++;
                return;
            }

            int maxLength = LogLineFormatter.GetMaxLength(entry, _includeCallerInfo);
            if (maxLength > _line.Length)
            {
                // Grow geometrically so a run of slightly longer lines doesn't reallocate each time.
                _line = new byte[Math.Max(maxLength, (int)Math.Min((long)_line.Length * 2, Array.MaxLength))];
            }

            int length = LogLineFormatter.Format(entry, _includeCallerInfo, _line);

            // Names the step for the fault message; both are literals, so this doesn't allocate.
            string operation = "rotating";
            try
            {
                // A line goes whole into the current file, or starts a fresh one: an empty file takes any line.
                if (_fileBytes > 0 && _fileBytes + length > _maxFileBytes)
                {
                    Rotate(startup: false);
                }

                operation = "writing";
                _stream.Write(_line, 0, length);
                _fileBytes += length;
                if (entry.Level >= _flushLevel)
                {
                    _stream.Flush();
                }
            }
            catch (Exception exception) when (IsIoFailure(exception))
            {
                EnterFault(operation, exception);
                _droppedCount++;
            }
        }
    }

    /// <summary>Flushes buffered lines to the operating system (not forced to the disk).</summary>
    /// <remarks>
    /// Thread-safe; doesn't allocate unless it faults. Does nothing when the sink is faulted. Never throws for I/O:
    /// a failed flush faults the sink.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The sink is disposed.</exception>
    public void Flush()
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            try
            {
                _stream?.Flush();
            }
            catch (Exception exception) when (IsIoFailure(exception))
            {
                EnterFault("flushing", exception);
            }
        }
    }

    /// <summary>Flushes buffered lines and closes the file. Later calls do nothing.</summary>
    /// <remarks>
    /// Thread-safe. Never throws: if the final flush fails, the file is closed anyway and <see cref="Fault"/> is
    /// set. <see cref="Write"/> and <see cref="Flush"/> throw <see cref="ObjectDisposedException"/> afterwards;
    /// <see cref="Fault"/> and <see cref="DroppedCount"/> stay readable.
    /// </remarks>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Stream? stream = _stream;
            _stream = null;
            try
            {
                stream?.Dispose();
            }
            catch (Exception exception) when (IsIoFailure(exception))
            {
                EnterFault("closing", exception);
            }
        }
    }

    private static bool IsIoFailure(Exception exception) => exception is IOException or UnauthorizedAccessException;

    // Called with _lock held, or from the constructor. Keeps the first fault and closes the file for good.
    private void EnterFault(string operation, Exception exception)
    {
        _fault ??= new EngineError(ErrorKind.Io, string.Concat("RotatingFileLogSink failed ", operation, " '", _path, "': ", exception.Message));
        Stream? stream = _stream;
        _stream = null;
        try
        {
            stream?.Dispose();
        }
        catch (Exception closing) when (IsIoFailure(closing))
        {
            // Already faulted: the first failure is the one reported. Dispose releases the handle even when its
            // final flush throws.
        }
    }

    // Called with _lock held, or from the constructor. On success the new current file is open and empty.
    [MemberNotNull(nameof(_stream))]
    private void Rotate(bool startup)
    {
        // Close first: Windows can't rename or delete a file while a handle without FileShare.Delete has it open.
        Stream? previous = _stream;
        _stream = null;
        previous?.Dispose();

        bool hasCurrent = File.Exists(_path);
        if (hasCurrent)
        {
            // Make archive 1 free by moving the run of archives 1, 2, ... up by one, oldest first. Only that run
            // moves: archives after the first free slot stay where they are. So the next rotation after one that
            // shifted the archives but couldn't rename the current file (the next run's startup, since that failure
            // faults this sink) finds slot 1 free and moves nothing, instead of ageing the archives a second time.
            // When all slots are taken, the oldest moves to MaxFiles, overwriting whatever an earlier run or a crash
            // left there, and is deleted below.
            int free = 1;
            while (free <= _keptArchives && File.Exists(ArchivePath(free)))
            {
                free++;
            }

            for (int n = free - 1; n >= 1; n--)
            {
                File.Move(ArchivePath(n), ArchivePath(n + 1), overwrite: true);
            }
        }

        // Delete what is now beyond the limit; at startup, also what an earlier run with a larger MaxFiles left.
        int last = startup ? RotatingFileLogSinkOptions.LargestMaxFiles : _keptArchives + 1;
        for (int n = _keptArchives + 1; n <= last; n++)
        {
            File.Delete(ArchivePath(n));
        }

        if (hasCurrent)
        {
            if (_keptArchives == 0)
            {
                File.Delete(_path);
            }
            else
            {
                File.Move(_path, ArchivePath(1), overwrite: false);
            }
        }

        _stream = _openFile(_path);
        _fileBytes = 0;
    }

    // A new, empty log file: one buffered stream, readable (tailable) by others while the sink writes.
    private static FileStream OpenFile(string path) => new(path, new FileStreamOptions
    {
        Mode = FileMode.Create,
        Access = FileAccess.Write,
        Share = FileShare.Read,
        BufferSize = FileBufferBytes,
    });

    private string ArchivePath(int number) =>
        string.Concat(_archivePrefix, number.ToString(CultureInfo.InvariantCulture), _archiveSuffix);
}
