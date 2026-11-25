namespace BrutalEngine.Core.Logging.Sinks;

/// <summary>Settings for a <see cref="RotatingFileLogSink"/>. Validated by its constructor.</summary>
/// <remarks>Immutable once constructed. The sink copies the values, so later changes have no effect.</remarks>
public sealed record RotatingFileLogSinkOptions
{
    internal const string DefaultFileName = "engine.log";
    internal const long DefaultMaxFileBytes = 10 * 1024 * 1024;
    internal const long SmallestMaxFileBytes = 64 * 1024;
    internal const int DefaultMaxFiles = 5;
    internal const int LargestMaxFiles = 100;

    /// <summary>
    /// The directory the log files go in. Must be an absolute (fully qualified) path; the sink creates it if it's
    /// missing. The host provides it (from the platform's user paths); the sink deliberately doesn't use the VFS,
    /// so logging works before the VFS exists and while crashing.
    /// </summary>
    public required string Directory { get; init; }

    /// <summary>
    /// The current log file's name, a bare file name with no directory part. Default <c>engine.log</c>. Archives
    /// are named <c>&lt;name&gt;.&lt;n&gt;&lt;extension&gt;</c>: <c>engine.1.log</c> is the newest.
    /// </summary>
    public string FileName { get; init; } = DefaultFileName;

    /// <summary>
    /// The size, in bytes, a file may reach before the sink rotates to a new one. Default 10 MiB; at least
    /// 64 KiB. A single line longer than this is still written whole, alone in a fresh file.
    /// </summary>
    public long MaxFileBytes { get; init; } = DefaultMaxFileBytes;

    /// <summary>
    /// The most files kept, counting the current one: <c>MaxFiles - 1</c> archives. Default 5; from 1 to 100.
    /// With 1, rotating deletes the current file instead of archiving it.
    /// </summary>
    public int MaxFiles { get; init; } = DefaultMaxFiles;

    /// <summary>
    /// The lowest level whose entries are flushed to the operating system as soon as they're written. Default
    /// <see cref="LogLevel.Warning"/>. <see cref="LogLevel.None"/> flushes only on <c>Flush</c> and <c>Dispose</c>.
    /// </summary>
    public LogLevel FlushLevel { get; init; } = LogLevel.Warning;

    /// <summary>Whether each line ends with the caller's <c> (file:line)</c>. Default <see langword="false"/>.</summary>
    public bool IncludeCallerInfo { get; init; }
}
