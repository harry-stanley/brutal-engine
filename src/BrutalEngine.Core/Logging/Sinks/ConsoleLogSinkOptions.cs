using System.IO;

namespace BrutalEngine.Core.Logging.Sinks;

/// <summary>Settings for a <see cref="ConsoleLogSink"/>. Validated by its constructor.</summary>
/// <remarks>Immutable once constructed. The sink copies the values, so later changes have no effect.</remarks>
public sealed record ConsoleLogSinkOptions
{
    /// <summary>
    /// Where entries below <see cref="ErrorThreshold"/> go. <see langword="null"/> (the default) means standard
    /// output, opened and owned by the sink. A stream given here must be writable; the sink doesn't own or
    /// dispose it.
    /// </summary>
    public Stream? Output { get; init; }

    /// <summary>
    /// Where entries at or above <see cref="ErrorThreshold"/> go. <see langword="null"/> (the default) means
    /// standard error, opened and owned by the sink. A stream given here must be writable; the sink doesn't own
    /// or dispose it. It may be the same stream as <see cref="Output"/>.
    /// </summary>
    public Stream? ErrorOutput { get; init; }

    /// <summary>
    /// The lowest level written to <see cref="ErrorOutput"/>. Default <see cref="LogLevel.Warning"/>.
    /// <see cref="LogLevel.None"/> sends everything to <see cref="Output"/>.
    /// </summary>
    public LogLevel ErrorThreshold { get; init; } = LogLevel.Warning;

    /// <summary>Whether each line ends with the caller's <c> (file:line)</c>. Default <see langword="false"/>.</summary>
    public bool IncludeCallerInfo { get; init; }
}
