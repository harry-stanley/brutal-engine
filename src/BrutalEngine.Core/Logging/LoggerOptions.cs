using System;
using System.Collections.Generic;

namespace BrutalEngine.Core.Logging;

/// <summary>Settings for a logger. Validated by the logger's constructor.</summary>
/// <remarks>Immutable once constructed; the logger copies what it needs, so later changes have no effect.</remarks>
public sealed record LoggerOptions
{
    internal const int DefaultMaxMessageBytes = 4096;
    internal const int SmallestMaxMessageBytes = 64;
    internal const int LargestMaxMessageBytes = 65536;

    /// <summary>
    /// Level of every category unless overridden with <c>Logger.SetLevel</c>. Default
    /// <see cref="LogLevel.Information"/>. <see cref="LogLevel.None"/> turns logging off.
    /// </summary>
    public LogLevel MinimumLevel { get; init; } = LogLevel.Information;

    /// <summary>
    /// The sinks every enabled entry goes to, in this order. May be empty, but not <see langword="null"/> or
    /// contain <see langword="null"/>. The logger doesn't own or dispose them.
    /// </summary>
    public required IReadOnlyList<ILogSink> Sinks { get; init; }

    /// <summary>Source of entry timestamps. Default <see cref="TimeProvider.System"/>.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>
    /// Maximum size of a formatted message in UTF-8 bytes, ellipsis included. Longer messages are truncated.
    /// Default 4096; valid from 64 to 65536.
    /// </summary>
    public int MaxMessageBytes { get; init; } = DefaultMaxMessageBytes;
}
